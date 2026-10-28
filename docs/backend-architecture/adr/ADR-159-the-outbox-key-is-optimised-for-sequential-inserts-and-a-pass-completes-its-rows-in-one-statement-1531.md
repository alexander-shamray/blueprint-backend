# ADR-159 — The outbox key is optimised for sequential inserts and a pass completes its rows in one statement (#1531)

**Decision.** Every service with an outbox migrates its `OutboxMessages`
primary key to `OPTIMIZE_FOR_SEQUENTIAL_KEY = ON`, in a migration named
`OptimizeOutboxKeyForSequentialInserts`, as SQL, since EF Core has no builder
for the option. [§9.4](../09-messaging.md)'s `OutboxDispatcher` completes a
pass's delivered rows in one `UPDATE`, joined to `OPENJSON` over their ids,
once the pass's last delivery has returned. That statement runs under no
cancellation token, so a stopped pass still completes what it delivered. A
failed row keeps its own fail statement. Each service's
`OutboxDispatcherTests` reads the option from `sys.indexes` on the migrated
database, and Ordering's holds a stopped pass to completing its rows.
**Why.** The stress run of 2026-10-28 named Ordering's outbox first with
three replicas, so
[ADR-158](ADR-158-the-stress-override-runs-orderingapi-at-its-charts-replica-floor-1525.md)'s
replicas did not move the knee. The SQL Server's per-index statistics named
the table's clustered key as its hottest wait: 35 052 s of page latch over
220 861 waits, against 10 629 s of `WRITELOG` across the whole instance. The
key is an identity, so every staged row lands on its last page. Under load the
dispatcher claims rows that fresh, and it completed each in an autocommit
statement of its own: one latch on that page and one log flush per row, from
every replica. The option lets SQL Server pace the inserts queueing on that
latch, and one statement per pass turns up to `ClaimBatchSize` latches and
flushes into one.
**Consequences.** A process killed part-way through a pass redelivers every
row the pass had delivered, up to `ClaimBatchSize`, where per-row completion
redelivered one. That is a duplicate, which at-least-once already promises
and which §9.5's inbox and §6.6's idempotent projections absorb. A graceful
stop does not widen it. A pass that outlives `LeaseSeconds` can now have its
delivered rows reclaimed and delivered again by another replica, not only the
rows it has not reached: §9.4's lease residual, widened. `ProcessedAt`
records when the pass ended, not when the row was delivered, so §13.6's
outbox gauges count a delivered row as pending until its pass ends. The
option is not in the EF model, so a database created from the model rather
than migrated lacks it, and §4.5's scaffold carries the migration as a
template one, so a new publishing service has it. It needs SQL Server 2019 or
later, and `OPENJSON` a compatibility level of 130 or more. Whether the knee
moves is measured by a rerun of the same stress plan, not argued here.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
