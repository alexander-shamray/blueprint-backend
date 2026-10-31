# ADR-186 — The inbox has no index on the time a row was handled (#1648)

**Decision.** Every service's `InboxMessages`, the BFF's included, loses
`IX_Inbox_HandledAt` and keeps no index but its clustered primary key on
`MessageId` and `Endpoint`. The retention purge's inbox statement is
unchanged: it still deletes `TOP (@BatchSize)` rows whose `HandledAt` is
past `RetentionPolicy.InboxWindow` and names no index, so it now scans the
clustered key ([§9.5](../09-messaging.md)). The migration is
`DropInboxHandledAtIndex`, a template migration a new service is scaffolded
with, pure consumer included.
**Why.** ADR-172's two-machine stress run `20261031-102448`, on `853dce80e`
to 2 800 users, read `sys.dm_db_index_operational_stats` before and after:
the index took 44.3 s of page latch over 225 633 inserts in Ordering, 0.196
ms a row and the run's second index after `catalog.StockLevels`, 12.2 s in
the BFF and 1.4 to 6.0 s in each of Notifications, Catalog, Payments and
Shipping. Query Store put 9.1 s of
Ordering's inbox insert's 14.7 s at step 5 on Buffer Latch. Every row is
stamped with the present as it is handled, so each insert appends to the
index's last page, the mechanism
[ADR-176](ADR-176-the-log-retention-index-is-keyed-on-the-notices-id-1607-supersedes-adr-175s-unchanged-retention-index.md)
removed from `NotificationLog`. ADR-176's remedy, the index keyed on the id
with the time included, buys nothing here: a nonclustered index carries the
clustered key, and the table holds that key and `HandledAt` alone, so the
rekeyed index would be a second copy of the table written by every insert.
Dropping it leaves the purge the same rows to read and each insert one
index to write rather than two.
**Consequences.** The purge reads the inbox where it sought a range of it:
each batch scans the clustered key until it holds `RetentionPolicy.BatchSize`
rows past the window, and the batch that comes back short reads every row,
so a pass costs up to `RetentionPolicy.MaxBatchesPerPass` scans of a table
the length of the window, once each `RetentionPolicy.Interval`. Most
integration events carry random ids, `Guid.CreateVersion7()` from the
mappers, which SQL Server orders by their random last six bytes, and
`NEWID()` from the migrators' republishers, so their aged rows lie spread
through the key and a batch reads many rows for each it deletes. The saga's
commands carry MassTransit's `NewId`, which SQL Server orders by time, so in
the services that receive them those rows meet on the page after the newest
of them, a wait this decision neither adds nor removes. The migration drops
the index in
its one transaction, a metadata change and a deallocation; its `Down`
rebuilds the index offline over every row the window keeps. A sharded key, a
computed bucket leading `HandledAt` as
[ADR-182](ADR-182-masstransits-inbox-is-cleaned-up-by-this-service-bucket-by-bucket-1634-supersedes-adr-032s-library-cleanup.md)'s
leads `Delivered` in MassTransit's `InboxState`, was refused on ADR-176's
argument: it keeps a hot page a bucket and adds a column, for a purge that
runs once a `RetentionPolicy.Interval` over `RetentionPolicy.InboxWindow`
rather than ADR-182's back-to-back passes over
`SagaInboxCleanupService.DuplicateDetectionWindow`. Nothing here was benched;
whether the latch leaves, and what the scan costs at the run's volumes, is for
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
next two-machine run to measure.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
