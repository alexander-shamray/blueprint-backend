# ADR-180 — The outbox key is random and its claim index is led by a bucket (#1623); supersedes ADR-161's sequential-key option

**Decision.** Every service's `OutboxMessages` key stops being an identity:
`Id` takes `OutboxMessage.RandomIdSql`, sixty-four random bits, as its
default, and stays the clustered primary key without
[ADR-161](ADR-161-the-outbox-key-is-optimised-for-sequential-inserts-and-a-pass-completes-its-rows-in-one-statement-1531.md)'s
`OPTIMIZE_FOR_SEQUENTIAL_KEY`. The table gains `ClaimBucket`, computed from
the key's low `OutboxMessage.ClaimBucketBits` bits and never stored, and
`IX_Outbox_Unprocessed` is keyed on `ClaimBucket` and then `OccurredAt`. The
dispatcher's claim asks each of `OutboxMessage.ClaimBuckets` buckets for its
oldest `ClaimBatchSize` claimable rows and leases the oldest
`ClaimBatchSize` of those, so a batch is still the table's oldest
([§9.4](../09-messaging.md)). The migration is `KeyOutboxOnRandomId`, a
template migration a new publishing service is scaffolded with.
**Why.** #1623 found EF Core's insert of a transaction's outbox rows among
the stack's slowest statements at the knee, waiting mostly on page latches
with ADR-161's option set: Shipping's 10.3 s of 14.7 s at 1 600 users, and
Ordering's 12.4 s of 15.9 s at 2 000. The stack's instance had restarted, so
its per-index statistics were gone, and a bench reproduced the wait on a
scratch database on the stack's SQL Server: the table in Shipping's shape
holding 200 000 delivered rows, 48 sessions each staging one to three rows a
transaction in EF Core's statement shapes, and three loops running the
dispatcher's claim, a 25 ms pause for a pass's publishes and its completion,
for 30 s, each variant twice. As built, the sessions staged 114 082 and
146 171 rows, with 515 284 and 476 160 ms of page latch on `PK_OutboxMessages`
against at most 18 766 ms on any other index. With one claim loop, a random
key took the key to 945 and 900 ms but moved the wait to
`IX_Outbox_Unprocessed`, 166 118 and 352 943 ms, and the inserts alone put
153 520 ms there with no claim running: an index keyed on the time a row was
staged fills at one end as an identity does. Keying that index on the random
key as well removed the wait but left the claim a sort of every unprocessed
row, 342 and 320 ms a claim at backlogs of 170 000 to 193 000 rows, with one
loop and with three, so it drained slowest when a backlog was deepest. This
decision staged 204 799 and 171 015 rows, with at most 1 914
and 3 495 ms on any index, and its claims took 39 and 45 ms at backlogs of
78 000 to 102 000 rows. The option on the identity key was set in every
as-built run, as
[ADR-169](ADR-169-inboxstates-key-keeps-its-page-latching-the-sequential-key-option-measured-to-move-nothing-1581-adding-to-adr-167.md)
and
[ADR-178](ADR-178-the-saga-outbox-is-keyed-on-its-inbox-message-index-and-the-inboxs-id-is-random-1624.md)
found for the saga's tables.
**Consequences.** The claim reads up to `ClaimBuckets` times `ClaimBatchSize`
index rows and holds their update locks until the statement ends, so a
second replica's claim skips rows the first will not lease; at the benches'
backlogs it took 39 and 45 ms where the identity's took 30 and 35, and the
dispatchers completed 102 900 and 93 100 rows against 109 200 and 116 900.
`Id` no longer records arrival order, which nothing reads it for: the claim
and the age gauges read `OccurredAt` and the purge `ProcessedAt`. Its random
bits can collide with a retained row's, which fails the staging command's
own transaction rather than an inbox's retry, at odds near one in ten
trillion a row for a table holding two million. SQL Server alters no
column's identity, so the migration drops and adds `Id`, each row drawing
its own value, and rebuilds the table twice in doing so, in time that grows
with the processed rows the purge's window keeps; a row an older dispatcher
had leased then completes nothing and is delivered again once its lease
ends, a duplicate §9.5's inbox absorbs. A statement writing the table needs
the session settings an index on a computed column does, which the filtered
indexes already asked of it. ADR-161's one-statement completion stands, and
`OptimizeOutboxKeyForSequentialInserts` stays in each service's history,
which this migration's `Down` restores. The figures are a bench's, staging
thousands of rows a second where the stack's insert ran a few times a
second, so they place the wait and do not size it: whether the knee moves is
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
two-machine run to measure.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
