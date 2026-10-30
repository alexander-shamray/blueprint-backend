# ADR-177 — The saga outbox is keyed on its inbox-message index, and the inbox's id is random (#1624)

**Decision.** MassTransit's `ordering.OutboxMessage` takes the library's own
inbox-message index over `InboxMessageId`, `InboxConsumerId` and
`SequenceNumber` as its clustered primary key, superseding
[ADR-167](ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md)'s
nonclustered key on `SequenceNumber`, which is no longer indexed alone; the
two inbox columns become `NOT NULL`, and the table's indexes on `EnqueueTime`
and on `ExpirationTime` are dropped. `ordering.InboxState` keeps
[ADR-171](ADR-171-inboxstate-is-clustered-on-its-message-key-1583-supersedes-adr-169s-identity-clustered-key.md)'s
clustered message key and its nonclustered primary key on `Id`, and `Id`
stops being an identity: its default is `CONVERT(bigint,
CRYPT_GEN_RANDOM(8))`. `OrderingDbContext` makes these changes on the mapping
the library builds, declaring none of it, and refuses to build the model when
any of them is no longer the shape MassTransit 8.5 gives it
([§7.2](../07-persistence.md)). The migration is `KeySagaOutboxOnInboxKey`.
**Why.** #1624 found the saga endpoint's three statements at the knee, the
`InboxState` insert and update and the delivery's `DELETE` from the outbox,
still waiting mostly on page latches after ADR-171.
`sys.dm_db_index_operational_stats` on the stack's instance, over the runs it
had served since 2026-10-28, put those waits on neither clustered key but on
the indexes kept in arrival order: 440 549 ms on `PK_InboxState`, 293 646 ms
on `PK_OutboxMessage`, 60 955 ms on `IX_OutboxMessage_EnqueueTime` and
13 596 ms on `IX_OutboxMessage_ExpirationTime`, against 28 884 ms on the
clustered `AK_InboxState_MessageId_ConsumerId`. The library's `UPDATE` finds
its row by `Id` and `RowVersion`, so a message's insert and both its updates
meet on the identity index's last page; every outbox row is staged on
`PK_OutboxMessage`'s last page and removed from it a transaction later; and
Query Store held no statement reading either time index, which serve the bus
outbox Ordering does not use. A bench reproduced it on a scratch database on
the stack's SQL Server: the two tables in Ordering's shape, 60 000 delivered
inbox rows, the library's cleanup once a second, and 48 sessions each running
[ADR-174](ADR-174-the-saga-outbox-runs-two-transactions-a-message-1603-supersedes-adr-032s-default-delivery-limit.md)'s
two transactions a message, staging one to three outbox rows, for 30 s, each
variant twice. As built, the sessions completed 31 006 and 33 111 messages,
with 644 and 565 s of page latch on `PK_OutboxMessage` and 15 and 24 s on
`PK_InboxState`. A random `Id` took `PK_InboxState` to 0.2 and 0.1 s.
Dropping the two time indexes as well took 40 943 and 44 751 messages, and
`PK_OutboxMessage` waited 721 and 771 s; an earlier pair of that variant read
19 574 and 44 528, the first with 456 s of row-lock waits, so the later pair,
run beside this decision, is the one cited. `OPTIMIZE_FOR_SEQUENTIAL_KEY` on
the outbox's key moved nothing, 628 and 642 s, as
[ADR-169](ADR-169-inboxstates-key-keeps-its-page-latching-the-sequential-key-option-measured-to-move-nothing-1581-adding-to-adr-167.md)
measured for `InboxState`. This decision completed 63 455 and 72 938
messages, with 0.9 and 0.4 s on `PK_OutboxMessage`.
**Consequences.** Three more settings on tables the library maps are ours,
each kept from drifting by refusal as ADR-167's and ADR-171's are;
`SagaOutboxPlanTests` reads every index of the outbox and the sequence's
identity, and `SagaInboxStateKeyTests` the id's default. With its inbox
columns `NOT NULL` the outbox can no longer hold a bus-outbox row, which
carries an `OutboxId` and no inbox message, and the migration fails rather
than key one it finds; a service that calls `UseBusOutbox()` takes this
decision up again, the time indexes with it. EF Core's conventions make only a
single-column key an identity, so the context keeps `SequenceNumber` one
itself; the delivery reads a message's rows in its order. `Id` no longer
records arrival order, which nothing here reads it for, since the cleanup
selects by `Delivered`; its sixty-four random bits can collide, which fails
one insert into the endpoint's retry to draw again, at odds below one in a
hundred million over the duplicate-detection window's tens of thousands of
rows. SQL Server alters no column's identity, so the migration drops and adds
`Id`, each row drawing its own value, and rebuilds both tables; the outbox is
near empty between deliveries. The next wait is `IX_InboxState_Delivered`, 194
and 222 s in this decision's runs: the library's cleanup index, to whose end
every delivery moves its row, which this decision leaves where it is. The
figures are a bench's, not the stack's; the knee's movement is
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
stress run to measure.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
