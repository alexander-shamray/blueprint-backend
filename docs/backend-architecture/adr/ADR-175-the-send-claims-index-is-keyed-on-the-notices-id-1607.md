# ADR-175 — The send claim's index is keyed on the notice's id (#1607)

**Decision.** Notifications' `IX_NotificationLog_SendClaim` keeps its name and
its `[Status] = 'Pending'` filter but is keyed on `NotificationId`, with
`NextAttemptAt`, `LockedUntil` and `Channel` included, where it was keyed on
`NextAttemptAt`; `SendClaims`' claim names the index in a table hint and still
takes its rows by `ORDER BY NextAttemptAt`, now as a sort. The migration is
`RekeySendClaimIndex`. `IX_NotificationLog_PendingOrder` and
`IX_NotificationLog_CompletedAt` are unchanged.
**Why.** #1607 found `NotificationLog`'s insert and outcome update waiting
mostly on page latches at the stress knee. SQL Server's per-index statistics,
read on the stack's instance after the stress runs it had served since
2026-10-28, put 2 270 424 ms of the table's page latch on the claim index and
1 232 051 ms on `IX_NotificationLog_CompletedAt`, against 32 291 on the
pending-order index and 1 561 on the clustered key, so a key that spreads the
table's inserts would move nothing. The claim index held four pages through
2 309 014 inserts and as many updates and removals. A bench on that instance
gave a scratch copy of the table 300 000 ended rows, then ran 16 sessions
each committing an event's two notices every 20 ms beside 8 or 48 sessions
each running `SendClaims`' claim for 20 rows and committing each row's
outcome alone, for 30 s a run, each variant twice. Requests sampled every
100 ms put the waits on the claim index's one leaf page, under the insert, the
claim's lease and the outcome, each of which writes it because a pending
row's due time is the newest. Keyed on the id, whose order SQL Server reads
as random, the three spread over the index's pages: with 48 senders its latch
fell from 92 776 and 128 335 ms a run to 11 159 and 9 892, and the rows sent
rose from 35 694 and 27 184 to 36 010 and 36 146; with 8, from 4 668 to
8 053 ms over four runs to 642 and 1 033. Without the hint the optimiser read
some claims through the pending-order index, which shares the filter, and
looked each row up, and the other sessions then waited 7.1 to 21.8 s a run on
row locks in that index.
**Consequences.** The claim reads every pending row and sorts the due ones,
where it read the oldest few in order, so a backlog the relay leaves behind
costs each pass a scan of the narrow index, and a second replica claiming at
the same instant can find the first's candidates locked and take nothing on
that pass. The hint ties the claim to the index's name; a rename fails every
claim and every test that claims. Three alternatives were measured and
refused: `OPTIMIZE_FOR_SEQUENTIAL_KEY` on both time-keyed indexes, 4 571 and
6 458 ms against 6 140 and 8 053 in the same sweep; dropping `LockedUntil`
from the include, which made the claim look up every leased row and left
21 845 and 21 801 rows pending; and folding the pending-order index into the
claim's, as good as this one but changing the seek that the erasure's and the
retention pass's deletes take under the lock order they argue. The bench
does not reproduce `IX_NotificationLog_CompletedAt`'s share, 84 to 1 969 ms a
run, and the option moved nothing there either, so it stays as it is and
#1607 keeps it. These are a bench's figures on one machine, with synthetic
statements in place of EF Core's `MERGE`; whether the knee moves is for
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
two-machine stress run to say.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
