# ADR-168 — The outbox lanes keep one claim after ADR-166 as well (#1570), adding to ADR-165 and ADR-166

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher` keeps one claim
and one pass for both lanes, as
[ADR-165](ADR-165-the-outbox-lanes-keep-one-claim-splitting-them-freeing-the-broker-lane-but-not-the-drain-1559-adding-to-adr-164.md)
kept it: measured again on
[ADR-166](ADR-166-the-order-summary-projection-writes-and-claims-in-one-batch-and-one-commit-1562-adding-to-adr-165.md)'s
shorter `Local` delivery, a claim of its own for each lane still empties the
`Broker` backlog, and now lengthens the `Local` drain it only failed to shorten
under ADR-165.
**Why.** ADR-165 said the split was worth measuring again once a `Local`
delivery was shorter, and #1570 asked for it. Four runs of the stress plan
(level `high`, stop rule `knee`) were made on 2026-10-29 on one stack at
`24b431098` with
[ADR-167](ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md)'s
clustered key, alternating one claim and ADR-165's split variant, each
knee-stopping at step 1, 3200 VUs, with Ordering exporting metrics every 5 s.
With one claim, a `Local` delivery took 52.3 and 40.8 ms over the load, its
backlog peaked at 3 800 and 3 274 rows and fell under 100 at 100 and 98 s
after the load, and the `Broker` backlog peaked at 2 462 and 1 975 rows. Split,
the `Broker` backlog never passed 245 and 143 rows and its oldest row 4.5 s,
but a `Local` delivery took 46.3 and 64.8 ms, its backlog peaked at 6 624 and
4 433 rows, its oldest row reached 84.3 and 54.8 s against 42.0 and 37.4 s,
and it fell under 100 at 141 and 140 s. The fulfilment saga, which writes to
the same database and log as the `Local` lane, received 60.1 and 36.6
messages a second split against 50.5 and 62.8 with one claim. So the drain
after a load is still set by the `Local` lane, and the split now slows it.
**Consequences.** Integration events still wait behind a pass's projections
under a load past the knee, for the oldest-row figures above, as ADR-165
recorded, and still count against §13.7's event end-to-end target. Two runs
each way show the direction both times; neither pair says how much of the
spread is noise. The split's variant and the saga's concurrency switch were
run-only patches and are not in the tree; a rerun applies them again.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
