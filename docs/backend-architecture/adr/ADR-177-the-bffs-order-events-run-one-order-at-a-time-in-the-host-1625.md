# ADR-177 — The BFF's order events run one order at a time in the host (#1625)

**Decision.** The BFF's `bff-order-events` endpoint partitions ADR-051's seven
order events by `OrderId` over one MassTransit partitioner of
`OrderPartitions` partitions, on each consumer's message pipe, so two events
for one order never run at once in one host. The endpoint's retry wraps that
pipe, so a failed attempt gives its partition up while it waits to retry;
`UsePartitioner` on the endpoint itself would sit outside the retry and hold
the partition for the whole ladder.
`ProductPublished`, on the same endpoint, is not partitioned. The
projection's statements, `MERGE ... WITH (HOLDLOCK)` included, are unchanged.
**Why.** #1625 found two of the projection's `MERGE` statements the largest
statement time at the first step of stress run 20261030-190623, nearly all of
it lock wait. Query Store on the stack's instance, over every stress run it
had served, named them: `ShipmentDispatched`'s and `ShipmentDelivered`'s,
299 225 runs each, with 1 508 s and 1 756 s of lock wait, about 5 and 6 ms a
run, against under 0.5 ms for each of the other five events' statements.
Shipping raises both events in one poll's transaction when the carrier's page
carries collection and delivery together, as the carrier simulator's default
feed always does, or delivery alone, so they reach the BFF together and run
in two sessions on one row; the second waits for the first's commit, which
on a log-bound instance is a log flush. `HOLDLOCK` is not the cause: on a row
that exists the `MERGE` takes a key lock alone, measured in
`sys.dm_tran_locks`, and a range lock only where the key is missing, which is
once an order. Lowering the endpoint's concurrency would serialise every
order to remove a wait between two events of one.
**Consequences.** The wait does not go away; it moves. The second event of a
pair now waits in the host for the first to finish, holding its concurrency
slot as before but no SQL Server session, worker or lock, so the projection's
staleness for that event is what it was. Partitions are hashed, so two
unrelated orders sharing a busy partition wait for each other where they did
not; `OrderPartitions` is set well past the endpoint's concurrency to keep
that rare, and each such wait is one handler's attempt. The partitioner is per
process: with more than one BFF replica, a pair delivered to two hosts still
meets on the row, and SQL Server's lock is what keeps it correct, as it was
before. Whether the knee moves is for
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
two-machine stress run to say.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
