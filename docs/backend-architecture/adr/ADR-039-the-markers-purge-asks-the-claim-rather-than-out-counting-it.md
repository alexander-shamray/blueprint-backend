# ADR-039 — The marker's purge asks the claim rather than out-counting it

**Decision.** [§9.5](../09-messaging.md)'s retention pass stops deciding a
marker's fate by comparing one window against another. It still selects
candidates by age — `CommittedAt` against a cutoff the database computes, which
is [ADR-038](ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md)'s
and is unchanged — and then asks `IIdempotencyStore.UnheldAsync` which of those
keys the claim store has already let go of, deleting only those. A marker whose
claim is still held survives its own window. `RetentionPolicy.IdempotencyWindow`
keeps its floor and stops being the thing that makes the ordering true.

**Why.** ADR-038 made the two *start* events ordered by construction and was
explicit that the two *expiries* were not. Redis expires the claim after
`IdempotencyRetention.Window` elapsed by **Redis's** clock; the purge deleted
the marker after `IdempotencyWindow` elapsed by **SQL Server's**. Nothing
couples those two rates, so a forward step of the database's clock — an NTP
correction, a host migration, a resumed snapshot, an operator setting the time
— carried the cutoff past a marker whose claim was still live. The claim then
expired into a table that had already forgotten the commit, and the next retry
claimed a free key and ran a committed command a second time: the duplicate
write [§8.5](../08-caching-redis.md) exists to prevent, arriving at the boundary
the floor is supposed to hold.

**A margin was not available, and this is the third time of asking.** ADR-037
put a five-minute `MarkerLeadAllowance` on the floor for two terms; ADR-038
removed both at the source and retired the allowance, recording that a number
would repeat the mistake in a third term rather than close it. That reasoning
applies here unchanged and more strongly: a clock step is bounded by nothing
this repository can assert, and the honest size of the number is unknown. What
absorbed it instead was the handler's runtime plus whatever the configured
window exceeded the floor by — six days on the shipped defaults, and **nothing
at all at the floor**, which is the value [§8.5](../08-caching-redis.md) and
`docs/pr-decision-log.md` both name as the supported way to buy back the late
retry ADR-037 costs. The one configuration this platform recommends narrowing
towards is the one with no margin left.

**The comparison was standing in for a fact, and the fact has an owner.** *The
marker outlives the claim* is arithmetic over two windows. *The claim is gone*
is a question, and the store that holds the claim is the only thing that can
answer it. Asking removes both clocks from the decision rather than
synchronising them — there is no quantity left to be counted at a rate — and it
is exact where the arithmetic was approximate. It costs one lookup per
candidate on a pass that runs hourly, batched into one pipelined round trip.

**Shape 1 from [#171](https://github.com/alexander-shamray/blueprint-backend/issues/171)
was to give the claim a database deadline, and it was not taken.** The claim
could carry an absolute deadline read from `SYSDATETIMEOFFSET()` instead of a
Redis TTL, which would make the whole comparison one clock's arithmetic end to
end. It costs §8.5 the property that **a claim expires without anybody running
a purge**: a held key would then depend on housekeeping to be freed, which is
the postponement ADR-037 refused when it declined to hold claims rather than
release them, arriving by a different route. It would also put a database round
trip inside the fast atomic exclusion whose whole value is that it is neither.
This decision keeps the TTL and moves the question instead.

**Consequences.**

- **The purge depends on the claim store, and a service that has markers
  without Redis no longer starts.** `RetentionPurgeService` takes
  `IIdempotencyStore` as a constructor argument, so the failure is a DI
  resolution error at startup rather than a pass that silently deletes what it
  should have asked about. That matches how the store is registered — "a
  service that has Redis has the store, and one that does not cannot
  half-have it" — and §4.5's scaffold ships both together.
- **An unreachable claim store purges no markers, and that is the safe
  direction.** `UnheldAsync` answers for every key or throws; there is no
  partial answer, because a key reported unheld because its lookup failed is
  the deleted marker this record exists to prevent. The throw reaches
  `ExecuteAsync`'s existing catch, which logs and retries next interval, so a
  Redis outage costs the **marker** pass rather than a guarantee. It costs no
  more than that, and the scope is worth stating precisely: `PurgeAsync` runs
  the outbox and the inbox first, so those deletions are already committed when
  `UnheldAsync` throws — nothing rolls back and nothing else is skipped. Markers
  accumulate while it lasts, which is the outbox's failure mode and not this
  one's.
- **The marker's pass is two statements where the other two are one, and the
  window between them holds one race this record does not close and one it
  had to.** A retry can re-claim a key between the `SELECT` and the `DELETE`,
  and the marker then goes while a claim is live. That much is **not a
  regression**: the same race existed when one `DELETE` did the work, because a
  retry arriving during that statement met the same outcome — the marker is
  deleted and the retry's transaction reads no marker. What bounds it is that
  both attempts are already past `Window` from the original commit, which is
  where §8.5's guarantee ends by design.
- **The other half WAS a regression, and it is closed by a version bound
  rather than by a predicate.** A key names a command and not a row. Past the
  guarantee the key is claimable, so a retry can *commit* under it and write a
  fresh marker — and with [§15.3](../15-cicd-deployment.md)'s three replicas, a
  second purger holding the same selected key can then delete that replacement:
  a row inside its window with a live claim behind it, after which the next
  retry runs the command a third time. The single statement could not do it,
  because it matched on age and a replacement is stamped `now`. **A key-only
  delete was therefore weaker than what it replaced**, which is the one thing
  this split was not allowed to be.
- **Repeating the age cutoff was the first fix and was wrong, which is recorded
  because the reasoning is the trap.** It restores what the single statement
  matched on, and it re-reads `SYSDATETIMEOFFSET()` to do it — so a forward
  step of the database's clock between the two statements makes the replacement
  look old enough and the stale delete takes it anyway. **An age against a
  moving clock cannot guard an ABA**, and this record exists because that clock
  moves; a fix that assumes it does not is this decision arguing against
  itself.
- **No predicate closes it, and the third failure is what settled the shape.**
  A bound on the newest selected `CommittedAt` survives a *forward* step, which
  defeats the age cutoff; a *backward* step defeats the bound; and the two
  together fall to a backward step followed by a correction, which leaves the
  replacement both below the bound and past a re-read cutoff. **An arbitrary
  clock cannot be out-predicated**, and a fourth attempt would have been the
  guess this appendix spends ADR-037 and ADR-038 refusing.
- **So the `DELETE` names the row rather than describing it.** It joins the
  `(Key, CommittedAt)` pairs the `SELECT` returned: the key names the command
  and the timestamp names the write, so a replacement — a different write —
  cannot be matched — **by construction, and the next consequence is the limit
  of that**. It is the property the single statement had for free, and buying
  it back is what the split owed. Rows are chunked at 900 rather than 1,000
  because each now costs two parameters against SQL Server's 2,100.
- **`(Key, CommittedAt)` is an identity by construction rather than by
  constraint, and the difference is this decision's last residual.** The key is
  the primary key and the timestamp is written once by a column default, so two
  writes under one key differ in it — but nothing *enforces* that. `CommittedAt`
  is a `datetimeoffset(7)` taken from `SYSDATETIMEOFFSET()`, with no uniqueness
  constraint, so a replacement colliding with the row a purger selected would
  be matched and deleted with its claim live: the ABA this join closes,
  re-entered through the identity itself.
- **What that needs is an exact coincidence rather than a magnitude, and the
  distinction is why it is recorded instead of fixed here.** Every hazard
  above — the two-clock comparison, the forward step, the backward step, the
  step and its correction — follows from a drift of *sufficient size* in a
  direction, which NTP corrections, host migrations and resumed snapshots
  genuinely produce. A collision needs the database's clock to be set to the
  same 100-nanosecond tick as a row at least `IdempotencyWindow` old. That is
  not a smaller probability of the same fault; it is a different fault.
- **A `rowversion` closes it and is owed rather than declined**
  ([#173](https://github.com/alexander-shamray/blueprint-backend/issues/173)).
  It is unique and monotonic per database and reads no clock at all, so it is
  the identity this join wants. It costs a column, a migration for each
  service, a sixth template migration in §4.5's scaffold and an Appendix D row
  — a schema change to every service, which is the shape this appendix gives
  its own record rather than folding into a pull request about something else.
  ADR-038 filed #171 on exactly those terms.
- **The defect and three successive wrong fixes were all found by review rather
  than by a test.** The interleaving needs several purgers, a retry and one or
  two clock steps, and nothing here stages one; what the suite does hold is
  that the identity join still deletes, which is the failure a join can have
  that a predicate cannot.
- **The candidates are ordered oldest first, and the pass stops when the claim
  store releases nothing.** That is the only state in which the next `SELECT`
  is certain to return the same rows, so it is the only one where continuing is
  certain to be futile. **Two earlier spellings were wrong, in opposite
  directions**, and both are worth carrying because each reads as obvious.
  Stopping on a *partial* batch rested on the premise that such a batch comes
  back unchanged — it does not, because `TOP` refills the deleted slots with
  the next-oldest candidates, so one held key at the head ended a pass after
  about one batch, 4,999 rows where the ceiling allows 100,000. Stopping on
  *nothing deleted* then read a zero from the wrong side of a race: with
  [§15.3](../15-cicd-deployment.md)'s three replicas another purger may delete
  every row this one selected before its own `DELETE`, and that zero is
  concurrent progress rather than a batch nobody may touch — stopping on it
  hands the backlog to the next hourly pass, or to nobody if the winning
  replica has since exited. What the store released does not depend on who won.
  What remains is bounded rather than absent: at a batch size of one, a held
  oldest key stops every pass until its claim expires, which is a day at
  `IdempotencyRetention.Window`, and a batch of one is not a configuration this
  platform ships.
- **The floor survives and its job changes, which is a smaller claim than it
  used to make.** `RetentionPolicy.IdempotencyWindow` may still not be shorter
  than `IdempotencyRetention.MarkerFloor`, and the refusal message no longer
  has to carry two assumptions: the ordering it protects is now enforced rather
  than derived. What the floor bounds is **how long the guarantee lasts** — set
  the window to the claim's own length and a marker becomes deletable the
  moment its claim expires, so *at most one commit per key while the marker
  survives* ends where the claim does. That is a real reduction in the promise
  and not a correctness hole, which is exactly the distinction the floor could
  not draw before.
- **Deleting by row rather than by predicate meets a limit belonging to
  another layer.** Each row costs two parameters — its key and its version —
  and SQL Server refuses a statement carrying more than 2,100, where the
  default `BatchSize` is 5,000. The delete is chunked at 900 rows so
  `BatchSize` keeps meaning rows considered per batch rather than being quietly
  capped.
- **What this does not close is the claim expiring under a running handler.**
  Past `Window` a successor may claim the key and both attempts run, and what
  keeps the loser from corrupting the winner's entry is still the claim token
  ([#127](https://github.com/alexander-shamray/blueprint-backend/issues/127)).
  That residual is §8.5's, it is unchanged, and it is now the only one — where
  ADR-038 left two and ADR-037 left three.
- **The test that proves it cannot be written from the clock's side, and the
  one that is written says so.** No suite here owns the database container's
  clock, so a forward step cannot be staged. What can be staged is the state
  the step produces: a marker thirty days old with a live claim behind it. The
  companion test releases that claim and watches the same row go, which is what
  makes the pair about the claim rather than about a purge that had stopped
  deleting markers.

> **The consequences about `(Key, CommittedAt)` are superseded by
> [ADR-041](ADR-041-the-markers-delete-identifies-a-row-by-a-rowversion-not-a-timestamp.md),
> and nothing here has been edited.** The decision this record took — age
> selects and the claim store decides — is untouched and still binding, and so
> is the argument that an arbitrary clock cannot be out-predicated. Deleting by
> row rather than by predicate is what ADR-041 builds on; it changes what
> identifies the row and nothing else.
>
> **What moved is the qualification this record made against itself**, and it
> is more than the sentence naming the residual. The bullet saying the `DELETE`
> "joins the `(Key, CommittedAt)` pairs the `SELECT` returned" is false of the
> code as of ADR-041 — it joins `(Key, RowVersion)` — and the three after it
> are the qualification itself: that the pair is an identity by construction
> rather than by constraint, that closing that needs an exact coincidence
> rather than a magnitude, and that a `rowversion` "closes it and is owed
> rather than declined". It is now owed no longer. **No count opens this
> paragraph on purpose**: the first draft said three, having enumerated the
> qualification and missed the bullet it qualifies.
>
> **Two details in that last consequence were wrong when it was written, and
> both are corrected here.** The scaffold gains a **seventh** template
> migration and not a sixth — §4.5 already shipped six — and the single
> "Appendix D row" it costs is two, the entity's and
> `RetentionPurgeService`'s. They are stated in this callout rather than in
> ADR-041's own consequences because a correction to a superseded record
> belongs beside the record it corrects, where a reader of ADR-039 will meet
> it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
