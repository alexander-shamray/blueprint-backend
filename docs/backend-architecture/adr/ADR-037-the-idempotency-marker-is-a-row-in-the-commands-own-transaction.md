# ADR-037 — The idempotency marker is a row in the command's own transaction

**Decision.** A command that opts into [§8.5](../08-caching-redis.md) writes a
marker row keyed on the idempotency key, inside the transaction
[§6.3](../06-cqrs.md) opens for it, and [§6.3](../06-cqrs.md) reads that row before
the handler runs. The Redis claim keeps its job — the fast, atomic exclusion
that makes a concurrent duplicate fail early — and the row is what makes the
*ambiguous* case decidable. The port is `IIdempotencyMarkerStore` in
`Common.Application`; the implementation writes through the service's own
`DbContext`, which is what puts it on the transaction's connection. The
marker's retention window may not be shorter than the Redis claim's, and
`RetentionPolicy` refuses one that is.

**Why.** §8.5's `IdempotencyBehavior` releases its claim on every exception out
of `next()`. One of those exceptions is raised over work that **already
committed** — a `CommitAsync` that succeeded on the server whose acknowledgement
was lost — and releasing there frees the key for a command that ran, so the
retry writes it twice. That is the outcome the behaviour exists to prevent,
arriving on the one path it cannot see. The race has been recorded as knowingly
open since PR-09, and §8.5 has carried it as a stated exception in its own
opening sentence ever since: *at most one commit per key within `Retention`,
except across a lost acknowledgement.*

**Redis cannot close it, and no cleverer use of the existing store can.**
`IIdempotencyStore` is a port over the coordination connection ([§8.1](../08-caching-redis.md),
[§8.3](../08-caching-redis.md)) — a different system from the one the transaction
commits to, so neither ordering of the two is atomic. Claim first and a lost
acknowledgement releases a claim over durable work; commit first and the claim
is not held while the work runs, so two concurrent requests both execute.

**Holding the claim instead of releasing it is not the fix either, and that is
the part worth being explicit about.** §8.5's release table already records
what holding buys: it *postpones* the duplicate rather than preventing it. Every
Redis entry has a TTL, so a held key expires and the attempt after that claims a
free key and runs the command a second time. It would also cost every ordinary
fault its retry for a day, which is a large availability price for a
postponement. A row has no TTL: it survives until something deletes it, and what
deletes it is a retention window this repository chooses.

**Consequences.**

- **§8.5's guarantee loses its exception and gains a different bound.** It was
  *at most one commit per key within `Retention`, except across a lost
  acknowledgement*; it is now *at most one commit per key while the marker
  survives*, and what bounds it is `RetentionPolicy.IdempotencyWindow` rather
  than the Redis TTL. Both the exception and the shorter bound go at once,
  because the row outlives the claim and answers the question the claim
  could not.
- **A refused retry is a 409 and not a replay, and it is reached two ways.**
  On the lost acknowledgement the attempt threw before it returned, so §8.5's
  store never recorded an outcome to hand back. On the commoner path the
  command succeeded and its payload *was* recorded, and then the claim expired
  at `Retention` while the marker lived on — so the retry finds the outcome
  gone rather than absent. `CommandAlreadyCommittedException` therefore says
  the command was applied and its result is **no longer available**, which is
  true of both; naming a cause would be wrong on one of them. A client that
  needs the outcome reads the resource.
- **A late retry that used to re-run is now refused, and that is the price of
  the bound moving.** While the guarantee ended at `Retention`, a retry after
  the claim expired ran the command a second time by design — the exception in
  the old sentence and the expiry in it were the same fact. Removing the
  exception necessarily removes the re-run: between the claim's expiry and the
  marker's purge, six days on the shipped defaults, a caller retrying a
  succeeded command gets a 409 where it used to get a fresh execution. That is
  a real loss against both a replay and a re-run, and it is strictly better
  than the duplicate write it replaces.
- **§6.3's transaction gains a read and a write it did not have.** The read is
  before `next()`, so a command that already committed does no work at all
  rather than doing it and losing it to a constraint violation on the way out.
  The write is after the failure guard and after §2.3's aggregate count, so a
  command this transaction is about to refuse leaves no marker to refuse its
  retry with.
- **The key crosses between the two behaviours on a scoped carrier.** §8.5
  builds `{subject}:{operation}:{commandId}` from a principal it binds and a
  `static abstract` member reachable only through its own
  `IIdempotentCommand` constraint; §6.3 is constrained to neither and would have
  to rebuild it by reflection. `IdempotencyContext` carries the built value, and
  §6.3 reads it **once**, before anything runs — a nested dispatch would
  overwrite it mid-transaction, and re-reading afterwards would mark the inner
  command's key against the outer command's rows.
- **Every service gains a table, a migration and a third retention purge.**
  `IdempotencyMarkers` sits beside `OutboxMessages` and `InboxMessages` in the
  service schema, mapped by an `IEntityTypeConfiguration` per service on
  [§7.2](../07-persistence.md)'s terms, and §4.5's scaffold ships it — a service
  scaffolded without it would fail a purge every hour and then fail the first
  idempotent command it was ever given, both against a table that is not there.
- **The window is a correctness setting wearing a housekeeping setting's
  clothes, so it is the one with a floor.** A purged outbox row loses a
  debugging record and a purged inbox row loses a suppression the broker will
  not exercise again; a purged marker re-opens the duplicate. Setting the window
  below the Redis claim's life leaves a stretch in which the key is claimable
  again and nothing remembers the commit, so `RetentionPolicy` reads
  `IdempotencyRetention.MarkerFloor` and refuses anything shorter rather than
  restating the number. **The floor is the claim's window plus
  `MarkerLeadAllowance`, and equality is refused rather than admitted as the
  exact fit.** Two things reorder the expiries: the marker is stamped inside
  the transaction while the claim is re-armed after it commits, so the claim's
  window starts later by the commit's tail; and the marker's age is the purging
  pod's clock against the writing pod's timestamp, across three replicas, so
  skew moves it again. Five minutes bounds their sum rather than removing
  either, and the two need different fixes —
  [#167](https://github.com/alexander-shamray/blueprint-backend/issues/167)
  for the clocks and
  [#168](https://github.com/alexander-shamray/blueprint-backend/issues/168)
  for the lag.
- **The uniqueness of the key is a backstop and not the mechanism.** Two
  attempts that reach the write concurrently produce a constraint violation
  rather than two rows; what makes that case rare is the Redis claim, and what
  makes it loud is the primary key. Neither is asked to do the other's job.

> **The floor consequence above is superseded by
> [ADR-038](ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md),
> and nothing here has been edited.** An ADR is superseded and never rewritten:
> the decision this record took — the marker is a row in the command's own
> transaction, read before the handler and written inside it — is untouched and
> still binding, and so is every argument for it, including the one that the
> window is a correctness setting and therefore the only one of the three with
> a floor.
>
> **What moved is the floor's *composition*, not its existence.** This record
> says the floor is the claim's window plus `MarkerLeadAllowance` and that
> equality is refused, and names the two things that reordered the expiries —
> the claim re-armed after a commit the marker was stamped before, and two
> pods' clocks ageing one row. Both were closed at the source rather than
> bounded, so `MarkerLeadAllowance` is gone and the floor is the claim's window
> exactly. The two paragraphs above that describe those terms are the reason
> the allowance existed, which is why they are worth keeping where somebody
> reaching for one will read them.

> **The same consequence is superseded a second time, by
> [ADR-039](ADR-039-the-markers-purge-asks-the-claim-rather-than-out-counting-it.md),
> and this is a separate note because the two records moved different things.**
> ADR-038 moved the floor's composition. ADR-039 moved its **job**, and in
> doing so disproved a sentence above that a reader would otherwise take as
> live: *setting the window below the Redis claim's life leaves a stretch in
> which the key is claimable again and nothing remembers the commit*. It does
> not any more. §9.5's purge deletes a marker only once the claim store reports
> the key gone, so a shorter window re-opens nothing — it asks for a guarantee
> shorter than the claim already gives, which is a setting that cannot do what
> it says. `RetentionPolicy` still refuses it, for that smaller reason, and its
> refusal message was rewritten rather than left arguing a failure the code no
> longer has.
>
> **What survives untouched is this record's decision and the argument that
> earned the floor at all**: the marker is a row in the command's own
> transaction, and its window is a correctness setting where the outbox's and
> the inbox's are housekeeping. That is why it is still the only one of the
> three with a floor, and why what the floor now bounds — how long the
> guarantee lasts — is worth bounding.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
