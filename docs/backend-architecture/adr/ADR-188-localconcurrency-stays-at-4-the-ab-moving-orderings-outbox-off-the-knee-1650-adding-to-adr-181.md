# ADR-188 — LocalConcurrency stays at 4, the A/B moving Ordering's outbox off the knee (#1650), adding to ADR-181

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher.LocalConcurrency`
stays at 4, as
[ADR-181](ADR-181-the-outbox-dispatcher-projects-local-rows-of-different-commands-side-by-side-1646-supersedes-adr-163s-serial-local-lane.md)
set it: a two-machine A/B across ADR-181 moved Ordering's outbox from the
first service to give up to one that kept up past the knee, with no deadlock
among the projection writers ADR-181 added, and no run varied the bound, so
there is no figure to tune toward. The A/B's second arm carried
other changes beside ADR-181, so it credits the move to them together. The
bound moves only on a run whose knee is Ordering's outbox again with its
`Local` lane staging more than it delivers.
**Why.** On 2026-10-31 ADR-172's two-machine run, the plan of
`20261031-102448`, ran once at a commit before ADR-181 and once at one after
it, both on fresh SQL Server and RabbitMQ volumes and the generator's machine
at the stack's commit. Run `20261031-180056`, at `853dce80e`, knee-stopped at
step 3, 2000 VUs, Ordering's oldest outbox row rising from 4.5 s to 17.0 s
over the hold. Run `20261031-173131`, at `7790e43ac`, knee-stopped at step 4,
2400 VUs, on the journey's p95 rising from 149 ms to 867 ms, and the first to
give up was Inventory's `FulfilReservationCommand`. At 2000 VUs the later
commit held Ordering's oldest outbox row at 0.5 s and confirmed 99.9% of
orders, against 17.0 s and 88.6% at the earlier one, at the same container
CPU; its outbox reached 1.8 s only at 2400. Between the two commits the
services also took
[ADR-182](ADR-182-masstransits-inbox-is-cleaned-up-by-this-service-bucket-by-bucket-1634-supersedes-adr-032s-library-cleanup.md)'s
cleanup of Ordering's saga inbox,
[ADR-186](ADR-186-the-inbox-has-no-index-on-the-time-a-row-was-handled-1648.md)'s
inbox without its `HandledAt` index,
[ADR-183](ADR-183-a-stock-level-is-padded-to-a-page-of-its-own-1647.md)'s
padded stock levels and
[ADR-184](ADR-184-migrations-run-under-a-raised-command-timeout-1644-adding-to-adr-007.md)'s
migrator timeout, and the first two take load off Ordering's database. SQL
Server's deadlock column read 0 at every step of both runs, so the 12
projection writers ADR-181 lets share Ordering's read tables produced none.
The lanes, which the A/B lost with its volumes, were read from run
`20261031-193139` at `3d6bb35a6`, after a 2400 s warm-up at 800 VUs, from
`ordering.OutboxMessages` by `OccurredAt` and `ProcessedAt` over each hold, as
ADR-181 read them:

| Step | Lane | Staged | Delivered | Mean delay, ms | Max delay, ms |
|---|---|---|---|---|---|
| 1, 800 VUs | `Broker` | 4 389 | 4 392 | 320 | 607 |
| 1, 800 VUs | `Local` | 8 776 | 8 774 | 258 | 607 |
| 2, 1600 VUs | `Broker` | 8 155 | 8 147 | 437 | 890 |
| 2, 1600 VUs | `Local` | 15 504 | 15 518 | 374 | 890 |

Both lanes delivered what they staged, the oldest row stayed at 0.7 s and the
deadlock column read 0. Step 2's `Local` lane delivered some 86 rows a second
across three replicas, under the 31 a replica ADR-181 measured its serial lane
at, so these rows show the lanes keeping up with ADR-181 in place and do not,
on their own, tell 4 from 1.
**Consequences.** The A/B is not the run ADR-181 asked for, each change on its
own: what share of the move is the bound's, and what share the inbox changes',
it cannot say. One commit run with the bound at 4 and at 1 is what would, and
none was run, so 4 is kept because nothing measured argues for another value,
not because 4 was measured best. Each arm is one run, and the morning run at
`853dce80e`, on a database holding the day's rows rather than a fresh one,
reached 2800 VUs before its outbox gave up, so the move is about a step, not a
measured bound. The lanes are read only up to 1600 VUs, where
`20261031-193139` knee-stopped on a tail at the generator rather than at the
gateway (#1677), so the steps that run never reached are where a larger bound
would show, if it would. Twelve writers on a read table is still argued safe
from three runs' deadlock column, not from a test that drives them into one. A
database holding days of rows changes the statements' cost (#1671), and none
of these runs is the comparison for one.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
