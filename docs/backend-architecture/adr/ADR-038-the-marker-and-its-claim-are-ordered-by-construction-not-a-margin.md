# ADR-038 — The marker and its claim are ordered by construction, not a margin

**Decision.** [§8.5](../08-caching-redis.md)'s Redis claim and [§6.3](../06-cqrs.md)'s
durable marker are ordered by the shape of the code rather than by a margin:
the claim's window **starts** before the marker is stamped, on the same thread
in the same dispatch, for every configuration `RetentionPolicy` admits. Two
changes are needed and neither is sufficient alone.
`IIdempotencyStore.CompleteAsync` **preserves what the claim had left** instead
of re-arming a fresh retention, so the claim's window runs from the claim.
`IdempotencyMarker.CommittedAt` is written by a `SYSDATETIMEOFFSET()` column
default and the marker's purge cutoff is computed in SQL, so the row's age is
one clock's arithmetic. `IdempotencyRetention.MarkerLeadAllowance` is retired,
`MarkerFloor` is `Window` unchanged, and a marker window equal to the claim's
is admitted.

**That the marker's expiry then falls after the claim's is a conclusion drawn
from that ordering, and this record does not decide it.** It needs the two
windows counted at the same rate, and they are counted by two servers — Redis
for the claim, SQL Server for the marker — which is
[#171](https://github.com/alexander-shamray/blueprint-backend/issues/171);
and it needs the marker to reach the database inside the claim's window,
which nothing here bounds. Both are stated under *Consequences* rather than
folded into the
decision, because a decision that claims what its own consequences take back is
the contradiction this appendix exists to keep out.

**Why.** ADR-037 put a correctness property at a boundary set by a retention
number, and then could not make the two numbers alone decide it. The rule it
needed — the marker outlives the claim — is a statement about two *instants*,
and the two instants were produced by different events on different clocks. A
five-minute allowance was added to the floor to cover both, which made every
admissible configuration safe for ordinary values of each and left the rule
itself untrue: nothing bounded either term, and nothing detected one exceeding
the allowance. **A margin that stands in for an unbounded quantity is a
guess with a number on it**, and the failure it guesses wrong about is a
silent duplicate write.

**The first term was the lag between two writes, and it was not the clock's
fault.** §6.3 stamps `CommittedAt` inside the transaction, before
`SaveChangesAsync`; §8.5's `CompleteAsync` runs only after `next()` has
returned, which is after the commit. Re-arming there started the claim's window
at the *commit* — later than the stamp by the commit's own tail, ordinarily
milliseconds and unbounded in principle, since a suspension or a stalled
connection between those two points stretches it. Redis then outlived the
marker by exactly that lag with perfectly synchronised clocks. Preserving the
claim's remaining life starts the window at `TryClaimAsync`, which precedes the
stamp on the same thread in the same dispatch — so the ordering is a
consequence of the code's shape rather than of any duration.

**The second was that one row was aged by two clocks.** `CommittedAt` was
stamped from the writing pod's `TimeProvider` and the purge cutoff computed on
whichever pod ran the purge; [§15.3](../15-cicd-deployment.md) ships three
replicas of each service, so those were routinely two machines. A purger whose
clock led the writer's by δ deleted the marker δ early, the claim then expired
into a table that had already forgotten the commit, and the next retry ran the
command a second time. A column default and a cutoff in SQL put both ends of
the comparison on the server that owns the row, and the term disappears rather
than being bounded.

**Only the marker moves to the database's clock, and the asymmetry with the
outbox and the inbox is the decision rather than an inconsistency to tidy
later.** [§9.5](../09-messaging.md) keeps those two on the registered
`TimeProvider` with a stated reason: a test host substitutes the clock, and a
row written on the server's wall clock is one no substituted clock can reason
about. That reason is worth more where the window is housekeeping — a purged
outbox row loses a debugging record, a purged inbox row loses a suppression the
broker will not exercise again — and worth less than a single clock where the
window *is* the guarantee. The marker's own suites stage rows at explicit ages
against the real clock rather than a substituted one, so nothing was traded
away to get this; a service that later needs the marker purge under a fake
clock is a service that needs a different seam, not a different column.

**Consequences.**

- **§8.5's replay window is now the remainder of the window the claim opened,
  and that is a change to what a caller is promised.** It was a full retention
  starting at the commit, so the stated twenty-four hours were twenty-four
  hours a caller actually got; it is now twenty-four hours from the claim,
  which a command that ran for an hour has spent an hour of. The guarantee that
  matters — at most one commit per key while the marker survives — is
  unaffected, and it is the one the mechanism exists for. A caller who needs
  the outcome after that reads the resource, exactly as ADR-037 already says.
- **`CompleteAsync` loses its `retention` parameter, and the absence is the
  contract.** A signature that still took one would invite the next
  implementation to use it. `TryClaimAsync` keeps its own, because the claim is
  where the window is opened.
- **The floor admits equality, and the sentence a review round once corrected
  is correct again.** `RetentionPolicy.IdempotencyWindow` may now be set to
  `IdempotencyRetention.Window` exactly — equal is admitted, and is the
  smallest window that is — which is what makes narrowing the window towards
  the claim's own length a supported way to buy back the late retry ADR-037
  costs. **Admitted is not gap-free**, and the two paragraphs above say why:
  a lower bound is what a floor is for, and this one refuses the windows that
  are certainly wrong rather than certifying the ones that are left. The
  floor is still read from `IdempotencyRetention.MarkerFloor` rather than
  restated, and
  that member survives its own arithmetic becoming trivial because what it
  names is a relationship between two windows and not a duration.
- **Every service gains a migration, and §4.5's scaffold ships it.**
  `IdempotencyMarkerCommittedAtDefault` alters one column in one table, and it
  travels with the table for the reason the table travels: a service scaffolded
  with the marker and without the default ages its rows on the writing pod's
  clock while the purge ages them on the server's, which is precisely the skew
  this record removes, reintroduced by omission on every new service.
- **Staging a marker at a controlled age still works, and the mapping is why.**
  `ValueGeneratedOnAdd` over a store default means EF omits the column while the
  property holds its sentinel and writes it when it does not — a default is
  what happens in the absence of a value, not a trigger overriding one. The two
  `RetentionPurgeTests` suites depend on that and are unchanged.
- **What this does not close is the claim expiring under a running handler.**
  Nothing here bounds the retention against a handler's runtime; past the
  claim's expiry a successor may claim the key and both attempts run, and what
  keeps the loser from corrupting the winner's entry is still the claim token
  (#127). That residual is §8.5's and is unchanged — and it is now the only one
  of the three, where it used to be one of three.
- **What holds by construction is the ordering of the two *start* events, and
  not that the two windows are counted by one clock.** This is the limit of the
  decision and it is stated here rather than left for a reader to find. Redis
  expires the claim after `Window` elapsed by *Redis's* clock; the purge deletes
  the marker after `IdempotencyWindow` elapsed by *SQL Server's*. Nothing
  couples the two rates, so a forward step of the database's clock relative to
  Redis's — an NTP correction, a host migration, a resumed snapshot — can carry
  the cutoff past the marker while the claim is still live, and what absorbs it
  is the handler's runtime plus whatever the window exceeds the floor by: six
  days on the shipped defaults, and nothing at all at the floor
  ([#171](https://github.com/alexander-shamray/blueprint-backend/issues/171)).
- **Reinstating an allowance is not the answer to that, which is why the floor
  is still `Window`.** Five minutes never bounded a clock step either, and a
  step is not bounded by anything this repository can assert — so a number there
  would repeat in a third term the mistake this record removes from two. What
  closes it is giving both deadlines one time source, which is a change to how
  the claim is stored and belongs to its own record.
- **A backward step of the server's clock is harmless on its own, and there is
  no second hazard here to state.** Both ends of the purge predicate —
  `CommittedAt` and `DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())` —
  read the clock the step moves, so a marker stamped after it is aged against a
  cutoff on the same shifted timeline and expires on schedule; the rows stamped
  before it survive the step's own size longer, which is the direction that
  costs nothing — a marker outliving its claim by more is what the floor is
  for. What is not harmless is the forward correction that follows, and that is
  #171's forward step rather than a hazard of its own.

> **The two consequences about the clocks are superseded by
> [ADR-039](ADR-039-the-markers-purge-asks-the-claim-rather-than-out-counting-it.md),
> and nothing here has been edited.** The decision this record took — the
> claim's window starts before the marker is stamped, by the shape of the code
> rather than by a margin — is untouched and still binding, and so is every
> argument for it. `CompleteAsync` still preserves what the claim had left and
> `CommittedAt` is still the database's clock; both are what ADR-039 builds on.
>
> **What moved is the term this record could only state.** It says the two
> windows are counted by two servers with nothing coupling their rates, that
> what absorbs a forward step is the handler's runtime plus whatever the window
> exceeds the floor by, and that closing it means one time source for both
> deadlines. The purge no longer counts a window against the claim's at all —
> it asks the store whether the claim is gone — so there are not two rates to
> couple. The paragraph refusing a third margin is the reason ADR-039 does not
> add one, which is why it is worth keeping where somebody reaching for one
> will read it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
