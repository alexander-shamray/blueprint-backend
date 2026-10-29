# ADR-169 — InboxState's key keeps its page latching: the sequential-key option measured to move nothing (#1581), adding to ADR-167

**Decision.** MassTransit's `ordering.InboxState` keeps its primary key as the
library maps it, with `OPTIMIZE_FOR_SEQUENTIAL_KEY` off: no migration sets the
option, and the table stays outside the three settings
[ADR-167](ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md)
made ours on `OutboxMessage`.
[ADR-161](ADR-161-the-outbox-key-is-optimised-for-sequential-inserts-and-a-pass-completes-its-rows-in-one-statement-1531.md)'s
option on §9.4's `OutboxMessages` keys is not reopened here.
**Why.** ADR-167 named page latches on `PK_InboxState`, an identity clustered
key, as the next wait on the saga endpoint, and #1581 asked whether ADR-161's
option, which paces the inserts queueing on such a key's last page, moves it.
Four runs of the stress plan (level `high`, stop rule `knee`) were made on one
stack at `0680052f0`, alternating the option off and on, with the saga
endpoint's concurrency at ADR-167's 48 and both of MassTransit's
`OutboxMessage` statements pinned to their clustered-seek plans through Query
Store, so no run could flip plan as #1570's one earlier try at the option did.
Off, the saga received 41.2 and 63.8 messages a second at 1 627 and 1 092 ms
a receive, its queue peaked at 2 307 and 3 318 and fell under 100 at 89 and
106 s after the load; on, 52.6 and 61.6 a second at 1 284 and 1 142 ms,
peaks of 4 062 and 3 421, and 128 and 94 s. `PK_InboxState` waited 3.59 and
3.96 million ms on page latches over the load off, 3.45 and 3.77 million on,
or 301 and 217 ms per inserted row off against 227 and 221 on. The two runs
with the option off are further apart than any pair across it. SQL Server's
requests, sampled every 200 ms, say why: in the second run off, about 15 000
samples waited on a page latch in an `InboxState` statement, 8 163 of them
the `UPDATE` under `PAGELATCH_EX`, 3 284 the `SELECT` under `UPDLOCK`, 1 736
the `UPDATE` under `PAGELATCH_SH` and 1 724 the `INSERT`. The library inserts
a message's row, then reads it under an update lock and updates it, all on the
newest page, and the option paces the insert alone; on, it added
`BTREE_INSERT_FLOW_CONTROL`, 311 s across the instance, in front of the
inserts, and left the reads and updates where they were.
**Consequences.** The latch stays the saga endpoint's largest wait past the
knee, 3.4 to 4.0 million ms a load, and #1583 tracks the lever this
measurement points at, clustering `InboxState` on the library's alternate key
over `MessageId` and `ConsumerId` as ADR-167 did for `OutboxMessage`, which
would be a second override of a table the library maps and is not taken
unmeasured. Every run stopped at step 1, 3 200 VUs, on the generator's CPU
rule rather than on the saga or the outbox, which #1582 tracks, so the figures
compare these four runs with one another and not with ADR-167's.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
