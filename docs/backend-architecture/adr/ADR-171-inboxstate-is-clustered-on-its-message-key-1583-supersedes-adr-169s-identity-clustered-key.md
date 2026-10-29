# ADR-171 — InboxState is clustered on its message key (#1583); supersedes ADR-169's identity-clustered key

**Decision.** MassTransit's `ordering.InboxState` is clustered on the
library's own alternate key, `AK_InboxState_MessageId_ConsumerId` over
`MessageId` and `ConsumerId`, and its primary key on the identity `Id` is
nonclustered, superseding
[ADR-169](ADR-169-inboxstates-key-keeps-its-page-latching-the-sequential-key-option-measured-to-move-nothing-1581-adding-to-adr-167.md)'s
decision to leave the key as the library maps it; ADR-169's refusal of
`OPTIMIZE_FOR_SEQUENTIAL_KEY` stands. `OrderingDbContext` makes
both changes after `AddTransactionalOutboxEntities()` on the keys the library
mapped, declaring neither, and refuses to build the model when the primary key
is no longer `Id` or no alternate key covers those two columns, as
[ADR-167](ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md)
does for `OutboxMessage` ([§7.2](../07-persistence.md)).
**Why.** ADR-169 found the saga endpoint's largest wait in page latches on
`PK_InboxState`'s last page, where the library inserts a message's row, reads
it under an update lock and updates it, and #1583 asked whether clustering on
the message key spreads those three statements. Whether it could was not a
given: of the table's rows after a load, about 37 000 carried MassTransit's
`NewId` ids, whose last six bytes, the ones SQL Server sorts a
`uniqueidentifier` by first, hold a timestamp, so they still sort in time
order, and about 30 000 carried `Guid.CreateVersion7` ids, whose last six
bytes are random. Four runs of the stress plan (level `high`, stop rule
`knee`) were made on one stack, alternating the key off and on by DDL on the
live database with the hosts unchanged, the saga endpoint's concurrency at
ADR-167's 48, both of MassTransit's `OutboxMessage` statements pinned to their
clustered-seek plans as in ADR-169, and the generator on the Compose network
([ADR-170](ADR-170-the-stress-runs-generator-runs-on-the-compose-network-1582.md)).
Off, `PK_InboxState` waited 5.52 and 5.50 million ms on page latches over the
load, about 20 000 and 21 000 of SQL Server's requests, sampled every 200 ms,
waited on a page latch in an `InboxState` statement, and the hottest page held
4 555 and 5 626 of those samples; on, the table's three indexes together
waited 0.35 and 0.45 million ms, about 1 100 and 1 500 samples, and the
hottest page 141 and 433, over 15 517 and 18 741 inserted rows against
15 041 and 17 676 off. The saga received 83.7 and 102.9 messages a second at
1 212 and 1 060 ms a receive off, 97.5 and 102.6 at 1 055 and 1 001 ms on;
its queue peaked at 2 123 and 3 921 off and fell under 100 at 84 and 94 s
after the load, and at 208 and 508 on, under 100 at 31 and 61 s; each run's
report named it among the first to give up over the hold at 983 to 1 873 and
1 423 to 2 823 off, and on not at all and at 252 to 508. Requests in flight
fell from 54 and 56 to 21 and 22, and the instance's largest waits became the
log's flush.
**Consequences.** A second table the library maps now has a setting of ours,
kept from drifting by refusal as ADR-167's is, and `SagaInboxStateKeyTests`
reads the clustered key and the saga outbox's foreign key. That key
references the alternate key, so the migration drops and restores it around
the rebuild, which EF Core leaves out because the key keeps its columns; the
table holds a message's row for the duplicate-detection window, so the
rebuild copies tens of thousands of rows after a load. The DDL first tried
for these runs ran outside a transaction against the running hosts, lost a
deadlock half way and left the foreign key dropped; the saga then consumed
nothing for about seven minutes until the key was rebuilt again in one
transaction, a stall whose cause was not established and whose backlog of
5 214 messages cleared in the first seconds of the first run on. The wait
moves rather than goes: on, the oldest row of [§9.4](../09-messaging.md)'s
outbox peaked at 52.2 and 50.4 s against 29.3 and 28.5 s off, and it is
what gave up first in all four runs. Every run knee-stopped at step 1, 3 200
VUs, with the machine's CPU at 90 to 93% over the hold, three of them ending
on the generator, so the figures compare these four runs with one another
and not with ADR-167's or ADR-169's.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
