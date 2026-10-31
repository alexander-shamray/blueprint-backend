# ADR-182 — MassTransit's inbox is cleaned up by this service, bucket by bucket (#1634); supersedes ADR-032's library cleanup

**Decision.** Ordering calls `DisableInboxCleanupService()` on
`AddEntityFrameworkOutbox` and registers `SagaInboxCleanupService` in its
place. `ordering.InboxState` gains `CleanupBucket`, computed from the low
bits of its random `Id` and never stored, and the library's
`IX_InboxState_Delivered` gives way to
`IX_InboxState_CleanupBucket_Delivered`, keyed on `CleanupBucket` and then
`Delivered` and filtered to delivered rows. Each pass deletes up to a
hundred rows from each of `OrderingDbContext.InboxStateCleanupBuckets`
buckets whose `Delivered` is older than
`SagaInboxCleanupService.DuplicateDetectionWindow`, the library's thirty
minutes, and starts the next pass at once when any bucket filled its batch,
as the library's loop does. `OrderingDbContext` makes the change on the
mapping the library builds and refuses to build the model when the index on
`Delivered` is no longer there ([§7.2](../07-persistence.md)). The migration
is `BucketInboxStateCleanupIndex`.
**Why.** #1634 found the index the next page latch of the saga endpoint once
[ADR-178](ADR-178-the-saga-outbox-is-keyed-on-its-inbox-message-index-and-the-inboxs-id-is-random-1624.md)
drew `Id` at random, and ADR-172's two-machine run `20261031-102448`
confirmed it on the stack: 40.9 s of page latch over 309 515 rows written at
2 800 users, 0.132 ms a row against 0.017 ms in the run to 1 600, the third
index of the run. Every delivery sets `Delivered` to the present, so it
moves its row to the index's last page, and every insert wrote the index
too, among the nulls. The library's cleanup orders by `Delivered` alone, so
an index led by anything else turns its range seek into a scan of every
delivered row, one that would meet rows a delivery holds. A bench on a
scratch SQL Server 2022 container reproduced it: the table in Ordering's
shape holding 60 000 rows delivered an hour before, 48 sessions each
inserting a row and then marking it delivered in two transactions, and one
loop running the cleanup, for 30 s, each shape twice. As built, with the
library's statement, the sessions completed 89 930 and 90 409 messages with
283 and 272 s of page latch on `IX_InboxState_Delivered`. This decision
completed 108 567 and 120 511, with 1.4 and 1.0 s on its index, and its
cleanup removed the same 60 000 rows in 56 and 57 passes.
**Consequences.** The second retention policy
[ADR-032](ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
took is now this service's code rather than the library's, so a library bump
that changes its cleanup changes nothing here, and a fix to it is ours to
make; its predicate is the library's, so an undelivered row is still never
removed, and the leak ADR-032 states stands. `DuplicateDetectionWindow` on
the outbox configurator now sets nothing, because only the library's cleanup
read it, and the window is the service's constant. `SagaInboxCleanupTests`
reads the index, the column and the statement's plan, which must seek the
index, and `MessagingRegistrationTests` that the library's cleanup is not
registered. A statement writing the table needs the session settings an
index on a computed column and a filtered index do, as
[ADR-180](ADR-180-the-outbox-key-is-random-and-its-claim-index-is-led-by-a-bucket-1623-supersedes-adr-161s-sequential-key-option.md)
found for the outbox; EF Core reads the computed column back after each
insert and update, one more column in the `OUTPUT` it already sends. A pass
runs sixteen statements where the library's ran one, each seeking a range
that ends at the window. The bench drew message ids at random, where
MassTransit's are ordered by time, and staged no outbox rows, so it places
the wait and does not size it: whether the endpoint's wait moves is
ADR-172's two-machine run to measure.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
