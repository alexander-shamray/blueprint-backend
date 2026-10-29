# ADR-166 — The order summary projection writes and claims in one batch and one commit (#1562), adding to ADR-165

**Decision.** [§6.6](../06-cqrs.md)'s `OrderSummaryProjection` sends each
write and its three [§13.3](../13-observability.md) claims as one batch in one
transaction, so a projected row is one round trip and one commit rather than
four round trips and a commit for each statement that writes, and
`RecordPendingFactsAsync` records a claim it won only once its reader has
passed the commit.
**Why.** #1562 asked where
[ADR-165](ADR-165-the-outbox-lanes-keep-one-claim-splitting-them-freeing-the-broker-lane-but-not-the-drain-1559-adding-to-adr-164.md)'s
`Local` delivery time goes. Three runs of the stress plan (level `high`, stop
rule `knee`) were made on 2026-10-29 on one stack, each knee-stopping at step
1, 3200 VUs, at 1 008, 1 001 and 1 041 requests a second, with Ordering
exporting metrics every 5 s as ADR-165's runs did, and SQL Server sampled
throughout: its Ordering requests every 200 ms, its waits every second and its
statements' totals every 5 s. As shipped, at `988e0f31c`, a `Local` delivery
took 103 ms over the step's ramp and hold, at 26.6 rows a second over three
replicas, each lane busy throughout, and 69.9 ms over the 100 s after it, the
window in which ADR-165 read 38 to 57 ms while Ordering's consumers still
staged rows. The statements themselves ran in 0.1 to 1.7 ms each. A
projection request was in flight in SQL Server for about 36 % of a delivery,
85 % of that in a commit's log flush, 16.8 ms a flush on the one instance
every service shares, and under 1 % in a lock wait; the rest of the delivery
was spent outside SQL Server, in and between its four round trips, with
Ordering using 3.3 of the machine's 24 cores. With one batch and one commit,
two runs: 38.0 and 36.9 ms a delivery over the ramp and hold, at 66.8 and 68.3
rows a second, and 26.5 and 29.1 ms over the 100 s after it; the
`Local` backlog peaked at 2 130 and 2 360 rows against 8 199 as shipped and
ADR-165's 7 078 to 8 704; the oldest row reached 25.3 and 28.1 s against
150.3 s and ADR-165's 103.6 to 132.5 s; and the backlog fell under 100 rows
65 and 75 s after the load against 225 s and ADR-165's 150 to 188 s. A
delivery is now in SQL Server for about 42 % of its time, 82 % of that in its
one commit's flush.
**Consequences.** A write's key lock and its `REPEATABLEREAD` read of the
order are held to the batch's commit rather than the write's own, three
primary-key updates longer: the order row is still held until the insert
commits, as
[ADR-137](ADR-137-a-placement-and-an-erasure-for-one-customer-take-one-application-lock-and-run-one-after-the-other-1191-amending-adr-099.md)
has it, only for longer. A batch that fails rolls its write and its claims back
together, where before a write could commit and its claims not run; the next
write on the row claims whatever is pending either way. A metric is still
recorded after the commit it describes, so a process that dies between the two
loses that count, as it did before. With the outbox no longer what gives out
first, both runs named Ordering's fulfilment saga queue as the first to give
up, 1 348 to 3 110 and 1 442 to 3 519 messages over the hold, as ADR-165's
split run did; this decision does not touch it. ADR-165's claim per lane, which
it kept for want of a shorter drain while the `Local` lane was what both lanes
waited on, is worth measuring again against these figures.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
