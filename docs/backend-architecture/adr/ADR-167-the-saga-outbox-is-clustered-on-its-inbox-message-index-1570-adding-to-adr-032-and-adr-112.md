# ADR-167 — The saga outbox is clustered on its inbox-message index (#1570), adding to ADR-032 and ADR-112

**Decision.** MassTransit's `ordering.OutboxMessage`, the table
[ADR-032](ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
puts behind [§9.6](../09-messaging.md)'s saga, is clustered on the library's
own unique index over `InboxMessageId`, `InboxConsumerId` and
`SequenceNumber`, its primary key on `SequenceNumber` is nonclustered, and
that index loses its filter, `[InboxMessageId] IS NOT NULL AND
[InboxConsumerId] IS NOT NULL`, which EF Core's SQL Server provider puts on a
unique index over nullable columns, because SQL Server clusters no filtered
index. `OrderingDbContext` makes the three changes after
`AddTransactionalOutboxEntities()` on the key and index the library mapped,
declaring neither, and refuses to build the model when either is no longer
the shape MassTransit 8.5 and EF Core's SQL Server conventions give it
([§7.2](../07-persistence.md)).
**Why.** #1570 asked why the fulfilment saga's queue was the first to give up
at the knee once
[ADR-166](ADR-166-the-order-summary-projection-writes-and-claims-in-one-batch-and-one-commit-1562-adding-to-adr-165.md)
freed the `Local` lane. A delivery from the saga's outbox finds its message's
rows with `DELETE … WHERE InboxMessageId = @MessageId AND InboxConsumerId =
@ConsumerId` and a matching `SELECT TOP(@p)`, and SQL Server keeps one cached
plan for each. Clustered on `SequenceNumber`, the plan compiled over a
near-empty table, as it is whenever Ordering is idle, scans the clustered
index rather than seeking the filtered one, and under load that scan waits on
the commit of every other delivery's rows: the queue, not the cycle,
[ADR-112](ADR-112-the-sagas-outbox-transaction-is-readcommitted-and-only-the-sagas-row-lock-is-serializable-992-supersedes-adr-032s-serializable-outbox-transaction.md)'s
first consequence foresaw. ADR-112 weighed this re-clustering and refused it
because under `Serializable` the bounded seek still locked to the next key,
the end of a near-empty table; ADR-112 itself made the transaction
`ReadCommitted`, which takes no range lock, so that reason no longer holds.
Query Store recorded both plans for the one `DELETE` on this stack: the seek
at 12.4 ms over 365 410 runs, and the scan, compiled at 04:06 UTC on
2026-10-29 when an index rebuild invalidated the cached plan, at 340.7 ms over
57 561. That flip confounded two of the runs made for #1570 before it was
found. Runs of the stress plan (level `high`, stop rule `knee`) were then made
on one stack at `24b431098`, each knee-stopping at step 1, 3200 VUs, with
Ordering exporting metrics every 5 s and SQL Server sampled every 200 ms. The
saga endpoint's concurrency was set to 48 in each, which is MassTransit
8.5.12's own default here, `Math.Max(Environment.ProcessorCount * 2, 16)` for
the 24 processors each replica sees, so the figure is this machine's and
not a constant. With the scan pinned through Query Store the saga received
13.7 messages a second at 5 140 ms a receive, 18.3 million ms of update-lock
waits fell on `PK_OutboxMessage` over the load, the queue peaked at 8 697 and
fell under 100 at 370 s after the load. With the seek pinned, two runs: 50.6
and 55.3 a second at 1 356 and 1 296 ms, no lock wait on the table, peaks of
4 797 and 3 669, and 131 and 95 s. Clustered on that index, with no plan
pinned, the optimiser chose a clustered seek for both statements on its own,
in two runs either side of the pinned one: 50.5 and 62.8 a second at 1 342
and 1 131 ms, peaks of 2 870 and 2 960, and 95 and 83 s. The saga queue still
grew over the hold, 1 347 to 2 242 and 1 234 to 2 507 messages against the
scan's 3 027 to 5 933, beside §9.4's outbox's oldest row, which is now named
first or with it. The key is no faster than the pinned seek: what it buys is
that no plan has to be pinned, or found pinned, for the seek to be the plan.
**Consequences.** Three settings on a table the library maps are now ours, which
is the second definition §7.2 warns of, kept from drifting by refusal rather
than by restatement: a MassTransit or EF Core bump that moves the primary key,
or the inbox-message index's columns, uniqueness or filter, stops every host,
test and migrator from building the model, naming this ADR, instead of being put
back by our block, and `SagaOutboxPlanTests` reads the clustered key and the
plan the `DELETE` compiles to over an empty table. Without its filter the index
also holds a bus-outbox row, which carries an `OutboxId` and no inbox message
id, clustered at the table's start; Ordering does not call `UseBusOutbox()`, and
a service that does takes this choice up again. The migration rebuilds the
table, which MassTransit's delivery keeps near empty. The next wait the seek
exposes is page latches on `InboxState`'s identity key, millions of ms over a
load, which this ADR leaves where it is.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
