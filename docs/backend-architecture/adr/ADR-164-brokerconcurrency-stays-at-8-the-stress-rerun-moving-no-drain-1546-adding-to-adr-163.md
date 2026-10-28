# ADR-164 — BrokerConcurrency stays at 8, the stress rerun moving no drain (#1546), adding to ADR-163

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher.BrokerConcurrency`
stays at 8, and `PassFilled`'s re-pass stays, as
[ADR-163](ADR-163-the-outbox-dispatcher-publishes-broker-rows-side-by-side-and-claims-again-at-once-after-a-full-claim-1535-adding-to-adr-131.md)
set them: the rerun ADR-163 owed measured neither change, nor both, moving
Ordering's drain after the load, so there is no figure between 8 and 16 to
tune toward, and what does set that drain is #1559's to measure.
**Why.** The 2026-10-28 15:36 stress plan (level `high`, stop rule `knee`)
was rerun four times overnight from 2026-10-28 on one stack at `c1d629123`,
Ordering's API rebuilt each time in one variant of its dispatcher: as before
ADR-163, `BrokerConcurrency` at 8 with the re-pass never asked for, the
re-pass with `BrokerConcurrency` at 1, and both as shipped. Each run
knee-stopped at step 1, 3200 VUs at 1042 to 1057 requests a second, and
Ordering's outbox, polled from the load's end, drained 11 634 rows in 165 s
before ADR-163, 11 575 in 159 s with the bound alone, 11 516 in 155 s with the
re-pass alone and 11 449 in 170 s with both: 67 to 74 rows a second net. The
oldest row at step 1's end read 56.2 s, 51.5 s, 56.7 s and 44.7 s. One run
each, those spreads are noise, and the 55 rows a second ADR-163 cites came
from an earlier stack, before
[ADR-161](ADR-161-the-outbox-key-is-optimised-for-sequential-inserts-and-a-pass-completes-its-rows-in-one-statement-1531.md),
so the same-stack run before ADR-163 is the comparison. In every run the
`Local` count rose for about 100 s after the load stopped, and then the backlog
emptied in 55 to 76 s; the two runs that also read leases and attempts had
every replica holding a full claim at every poll but one, with no row retried.
Over those 100 s, Tempo's sampled `outbox deliver` spans put a `Local`
delivery at 42 ms median and a publish at 8 ms. A pass ends when both lanes
have, and its `Local` rows go one at a time, so they set its length, and more
publishes in flight shorten nothing. The bound was not run at 16 because 1 and
8 measured the same.
**Consequences.** A pass still holds up to eight publishing scopes a replica
for no measured gain on this machine's broker, kept because it measured no
loss either, and a broker further away, where each publish waits longer, is
where it would count; that is argued, not measured. Net drain after a load
mixes the dispatcher's rate with Ordering's consumers still staging rows from
queued messages. The dispatcher counts neither lane itself: MassTransit's
`messaging.masstransit.send` counts its publishes among Ordering's other sends,
and `projection.lag` is recorded once for each `Local` row delivered, but on
this stack each series kept three to five points in ten minutes, too few to
read a rate over a drain of three minutes, so the figures here came from SQL
polls and 10% of traces. #1559 is to give the dispatcher a per-lane delivery
count that a drain can be read from, and to measure the `Local` lane and the
shared claim, each on its own, against these figures.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
