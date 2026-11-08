# ADR-189 — Saga inbox cleanup runs on one replica and yields to lock waits (#1671), adding to ADR-182

**Decision.** A pass of `SagaInboxCleanupService`, which
[ADR-182](ADR-182-masstransits-inbox-is-cleaned-up-by-this-service-bucket-by-bucket-1634-supersedes-adr-032s-library-cleanup.md)
made, first asks SQL Server for the session application lock
`SagaInboxCleanupService.PassLock` with no wait, on a connection it keeps
open for the pass; a replica that does not get it removes nothing until its
next pass. The replica that gets it sets the session's lock timeout to
`SagaInboxCleanupService.LockTimeout`, 100 ms, and a bucket whose statement
meets a lock held longer is given up until the next pass, which that
bucket's failure does not bring forward. The lock is released before the
connection goes back to the pool. Which rows a pass removes, the buckets,
the batch and the window are ADR-182's and do not change.
**Why.** ADR-172's two-machine run `20261101-171739`, the first to read each
lock wait and its holder, put the cleanup first among the platform's lock
waits at every step from 2 000 users: its `DELETE` spent 16.5 s of a
180 s hold waiting on locks at 1 600 users and 247.9 s at 3 200, where the
saga's queue grew from 3 843 to 6 911 messages and fulfilment's p95 reached
66.7 s. Two waits made that time. Deleting an `InboxState` row checks that no
`OutboxMessage` row still names it, through
`FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId`, and that check
reads under locks even under
[ADR-185](ADR-185-every-service-database-runs-read-committed-snapshot-1653.md)'s
row versioning, so it waited on the outbox rows of consumers still in their
transactions. Those rows are keyed by message id, and the ids
`Guid.CreateVersion7` draws sort at random
([ADR-171](ADR-171-inboxstate-is-clustered-on-its-message-key-1583-supersedes-adr-169s-identity-clustered-key.md)),
so a check can land anywhere in the index. And
each of Ordering's three replicas ran every bucket, so a second replica's
`DELETE` waited on the first. While it waited, a `DELETE` held its locks on
`IX_InboxState_CleanupBucket_Delivered`, which a consumer's own update of
`InboxState` writes when it stamps `Delivered`.
**Consequences.** Under load the cleanup falls behind rather than slowing the
consumers: a bucket it gives up waits for the next pass to take the lock, at
most the service's interval and sooner when another bucket filled its batch
or another replica's timer comes first; its rows stay past the window, and
`InboxState` grows until the load eases, which a deployment under sustained
load would read as a table that keeps growing. One replica cleaning removes
at most what one did before, so the cleanup's own rate is a third of what
three replicas could reach when nothing contended; no run has yet measured
one replica against the rate rows pass the window, and a run whose expired
rows outrun it is what reopens this. A replica stopping mid-pass ends its
session and so its lock, so the next replica's pass takes it over. The
foreign key stays, because it is MassTransit's model and dropping it would
leave an inbox row's removal unchecked against its outbox rows. Whether the
lock wait moves off the knee is the next run of the same plan to measure.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
