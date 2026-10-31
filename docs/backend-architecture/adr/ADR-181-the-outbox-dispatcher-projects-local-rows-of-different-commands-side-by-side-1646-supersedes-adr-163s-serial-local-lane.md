# ADR-181 — The outbox dispatcher projects Local rows of different commands side by side (#1646); supersedes ADR-163's serial Local lane

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher` groups a pass's
`Local` rows by `CorrelationId`, the command that staged them, and projects
up to `OutboxDispatcher.LocalConcurrency` groups at once, a group's rows one
after another, where
[ADR-163](ADR-163-the-outbox-dispatcher-publishes-broker-rows-side-by-side-and-claims-again-at-once-after-a-full-claim-1535-adding-to-adr-131.md)
projected every `Local` row of a pass one at a time. The bound is 4. The
`Local` loop takes no token of its own, so a stop after the pass's last
`Local` row returns what the pass delivered, as the serial loop did, and each
row's delivery still stops on the pass's token. Ordering's
`OutboxDispatcherTests` holds rows of different commands to the bound, and to
more than one at a time, and one command's rows to one.
**Why.** ADR-172's two-machine stress run `20261031-102448`, at `853dce80e`,
knee-stopped at step 5, 2800 VUs, on Ordering's oldest outbox row. Its rows,
read back from `ordering.OutboxMessages` by `OccurredAt` and `ProcessedAt`,
say which lane set the rate. Over step 2's hold both lanes kept up: 16 887
`Local` rows staged and 16 894 delivered, 8 628 and 8 626 `Broker`. Over step
5's, 21 090 `Local` rows were staged and 16 615 delivered, 92 a second across
three replicas, and 13 484 and 10 585 `Broker`; through steps 3 and 4 the
`Local` lane had delivered up to 107 a second. So each replica's one
projection at a time ran at about 31 rows a second, some 32 ms a row, and a
full claim's 100 rows held about 61 `Local` ones, some 2 s of serial
projection beside 39 publishes eight at a time. The `Local` loop set the pass,
and the pass set both lanes' rate, so a larger `BrokerConcurrency` would have
moved nothing, and a larger claim only a longer pass at the same `Local` rate;
a claim for each lane was measured twice and kept out
([ADR-165](ADR-165-the-outbox-lanes-keep-one-claim-splitting-them-freeing-the-broker-lane-but-not-the-drain-1559-adding-to-adr-164.md),
[ADR-168](ADR-168-the-outbox-lanes-keep-one-claim-after-adr-166-as-well-1570-adding-to-adr-165-and-adr-166.md)).
ADR-163 kept `Local` rows serial against two projections deadlocking on one
read table, but three replicas already project side by side, and the run
recorded no deadlock at any step. A command's rows are what most often write
one read row, so they stay serial; in step 5's hold every `Local` row was its
command's only one.
**Consequences.** The projection writers on a read table now number the
replicas times `LocalConcurrency`, 12 for Ordering's three, where ADR-163 held
them to three. A deadlock between two of them fails the victim's delivery,
which backs off on §9.4's ladder like any other failure, so it costs that
row's lag rather than the row, and the stress summary's deadlock column is
where it would show. One command's rows are serial within a pass only:
across replicas, or across passes, nothing orders them, as before. The bound
is a constant chosen, not measured; whether it moves the knee is a rerun of
ADR-172's two-machine run, each change on its own, as ADR-163's
`BrokerConcurrency` was. §9.4's lease residual now reads a `Local` pass as its
rows' delivery over `LocalConcurrency`, but never less than its largest
command's rows one after another, since one command's rows take turns.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
