# ADR-163 — The outbox dispatcher publishes Broker rows side by side and claims again at once after a full claim (#1535), adding to ADR-131

**Decision.** [§9.4](../09-messaging.md)'s `OutboxDispatcher` delivers a pass's
`Broker` rows up to `OutboxDispatcher.BrokerConcurrency` at a time and its
`Local` rows one at a time, the two lanes side by side, each row still in its
own scope and its own `try`. The rows share the claim's connection, so their
fail statements take a one-slot semaphore in turn. A pass whose claim came back
holding `ClaimBatchSize` rows and delivered at least one starts the next pass at
once, rather than at the next `PollInterval` tick; a short claim, a full one
that delivered nothing, and a pass that throws wait for the tick. The loop is
[ADR-131](ADR-131-the-switched-polling-loop-is-held-once-in-commoninfrastructureswitchingswitchedpollingworker-1083-adding-to-adr-113.md)'s,
so `SwitchedPollingWorker` gains `PassFilled`, which a worker overrides to ask
for that pass, and reads the operator switch before it as before any other.
`SwitchedPollingWorkerTests` holds the loop to running the next pass at once,
reading the switch first and ending on a stop; Ordering's
`OutboxDispatcherTests` holds `Broker` rows to the bound, and to more than one
at a time, and `Local` rows to one.
**Why.** After the stress run of 2026-10-28, Ordering's outbox held 26 000
`Broker` and 40 000 `Local` rows, the oldest 566 s, and drained them at 55 rows
a second net with no load on. Each publish is a round trip to the broker that
waits on nothing else, and the dispatcher awaited them one after another, so a
lane drained at one round trip a row whatever the broker could take; and a full
claim, which says more rows are waiting, still waited out `PollInterval` before
the next. `Local` rows stay serial because two projections writing one read
table at once is the deadlock `ProjectionInvoker` already runs its handlers in
sequence to avoid. Replicas already overlap their projections, so serial
delivery does not remove that risk; it holds the writers on a table to one a
replica rather than the bound a replica. Order is not the reason: §9.4 promises
none across rows, and a failed row already backs off while later ones proceed. A
full claim that delivered nothing waits, because then the lane is failing, and
the tick is that failure's backoff.
**Consequences.** A pass now holds up to `BrokerConcurrency` publishing scopes,
and as many publishes in flight, beside one projecting scope, from each replica.
Rows within a pass reach the broker in no fixed order, where before they went
one at a time in the order the claim returned them, which nothing was entitled
to rely on. A run of full claims runs pass after pass with no pause, so the
operator switch is read between them, and a stop cancels the pass under way and
the run starts no other. The `PeriodicTimer` keeps one tick it missed during
such a run, so the first wait after it is short. The bound is a constant, not an
option, chosen at 8 from the issue's 8 to 16 and not yet measured. Whether the
drain rate moves is measured by a rerun of the same stress plan, each change on
its own, not argued here.

**Superseded in part by
[ADR-181](ADR-181-the-outbox-dispatcher-projects-local-rows-of-different-commands-side-by-side-1646-supersedes-adr-163s-serial-local-lane.md)**:
the Decision's `Local` rows one at a time, the test that holds them to one,
the reason they stay serial and the consequence of one projecting scope a
replica. The `Broker` lane's bound, the next pass after a full claim and
their consequences stand.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
