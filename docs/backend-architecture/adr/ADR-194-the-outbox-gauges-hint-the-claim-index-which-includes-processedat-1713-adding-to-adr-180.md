# ADR-194 — The outbox gauges hint the claim index, which includes ProcessedAt (#1713), adding to ADR-180

**Decision.** Each publishing service's `OutboxStats` reads the outbox
through a table hint naming `OutboxMessage.UnprocessedIndex`,
`IX_Outbox_Unprocessed`, in all three of its queries
([§13.6](../13-observability.md)). That index, keyed as
[ADR-180](ADR-180-the-outbox-key-is-random-and-its-claim-index-is-led-by-a-bucket-1623-supersedes-adr-161s-sequential-key-option.md)
keys it, includes `ProcessedAt` beside `Lane`, `Attempts` and `LockedUntil`
([§9.4](../09-messaging.md)), though the filter admits no row where it is set.
The migration is `IncludeProcessedAtInClaimIndex`, which rebuilds the index in
one `CREATE INDEX ... WITH (DROP_EXISTING = ON)`, and a template migration a
new publishing service is scaffolded with.
**Why.** #1713 found Ordering's pending and abandoned counts on a plan
reading about 19 700 pages and 130 ms of CPU a call through stress run
20261102-130425, against 2 pages once a recompile chose the filtered index:
the outbox then held about 219 000 processed rows, all in the clustered
index the plan scanned. A scratch database on the stack's SQL Server, in
§9.4's shape before this decision, reproduced the cause: compiled on an
empty table, the age query and both counts scan `PK_OutboxMessages`, and
the plan stays cached as processed rows wait for the purge. A hint alone was
not enough. Without `ProcessedAt` in the index, SQL Server looks every row
up in the clustered index to test an `IS NULL` filter, so with 5 000
unprocessed rows the hinted count read 7 937 pages and the age 7 958. With
the column included, each read 39, and the unhinted count chose the index by
itself on an empty table, though the age still did not.
**Consequences.** The hint fails the statement, error 308, wherever the
index is missing or renamed, which `OutboxMetrics` contains into an absent
series (§13.6); each service's `OutboxStatsTests` reads the cached plans
and fails first. The index's rows grow by the column's ten bytes. The
rebuild holds a schema lock on the table for its length, and it cannot
build from the old index, which lacks the column it adds: on the bench it
read the whole clustered index, 8 776 pages for 55 000 rows of which 5 000
were unprocessed, so its length grows with the processed rows the purge's
window keeps, as
[ADR-184](ADR-184-migrations-run-under-a-raised-command-timeout-1644-adding-to-adr-007.md)'s
timeout allows for. The claim and the gauges wait on it, and a gauge that
waits past `CommandTimeoutSeconds` drops its series for that collection.
Whether the claim also gains from the column was not measured.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
