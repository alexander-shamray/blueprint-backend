# ADR-165 — The outbox lanes keep one claim, splitting them freeing the Broker lane but not the drain (#1559), adding to ADR-164

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher` keeps one claim
and one pass for both lanes, as
[ADR-163](ADR-163-the-outbox-dispatcher-publishes-broker-rows-side-by-side-and-claims-again-at-once-after-a-full-claim-1535-adding-to-adr-131.md)
left it, so the `Broker` rows a pass did not claim still wait for its `Local`
rows before the next claim: the one change that would free them, a claim of
its own for each lane, measured emptying the `Broker` backlog without
shortening the drain after a load, and it left the `Local` lane as slow as
before or slower.
**Why.** #1559 asked what
[ADR-164](ADR-164-brokerconcurrency-stays-at-8-the-stress-rerun-moving-no-drain-1546-adding-to-adr-163.md)'s
drain is set by, and §13.3's `outbox.delivery.duration` now answers it per
lane. Three runs of the stress plan (level `high`, stop rule `knee`) were made
on 2026-10-29 on one stack at `0326adaa0`, each knee-stopping at step 1, 3200
VUs, with Ordering exporting metrics every 5 s rather than the SDK's 60 s, so
a three-minute drain held enough points to read: two as shipped, and one with
each lane claimed on its own, `Broker` claims repeating while a pass's `Local`
rows ran. In all three every replica's `Local` lane was busy throughout, its
delivered rate times its mean delivery time near 1, from the load until about
100 s after it; a `Local` delivery took 38 to 57 ms while Ordering's consumers
still staged rows from queued messages, and 9 to 26 ms once they stopped, when
the backlog emptied. Shipped, the shared claim held `Broker` rows to about 45 a
second for those 100 s against a 5 to 15 ms publish, and their backlog peaked
at 4 748 and 4 768 rows. Split, it never passed 220, but the `Local` backlog
peaked at 8 704 rows against 7 078 and 8 096, the oldest row reached 132.5 s
against 103.6 s and 107.1 s, the backlog fell under 100 rows at 164 s after
the load against 150 s and 188 s, and the knee's first to give up became
Ordering's fulfilment saga queue, 3 065 to 6 410 messages over the hold,
beside the outbox. One run of the split cannot say whether its `Local` cost is
noise, but the drain it was meant to shorten did not move, and what sets that
drain is the `Local` lane's delivery time while rows still arrive, which
#1562 is to measure.
**Consequences.** Under a load past the knee, integration events wait in the
outbox behind a pass's projections, for as long as the oldest-row figures
above, and that counts against §13.7's event end-to-end target: the split
would have removed that wait, so this ADR keeps a known cost for want of a
measured gain in the drain. The split is worth measuring again once #1562 has
shortened a `Local` delivery, when the `Local` lane would no longer be what the
two lanes both wait on. The 5 s export was a run-only override, never
committed, since the game-day harness's timings assume the SDK's default; a
rerun needs it again, or reads a drain from SQL polls as ADR-164 did.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
