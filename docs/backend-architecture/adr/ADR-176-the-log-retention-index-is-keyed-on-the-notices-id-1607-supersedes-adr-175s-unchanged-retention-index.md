# ADR-176 — The log retention index is keyed on the notice's id (#1607); supersedes ADR-175's unchanged retention index

**Decision.** Notifications' `IX_NotificationLog_CompletedAt` keeps its name
and its `[CompletedAt] IS NOT NULL` filter but is keyed on `NotificationId`,
with `CompletedAt` and `Status` included, where it was keyed on `CompletedAt`.
The retention pass's purge is unchanged: it still takes the oldest ended
notices by `ORDER BY n.CompletedAt` and names no index. The migration is
`RekeyCompletedAtIndex`. This supersedes ADR-175's leaving the index
unchanged; ADR-175's claim index and the shapes it refused stand.
**Why.** [ADR-175](ADR-175-the-send-claims-index-is-keyed-on-the-notices-id-1607.md)
rekeyed the send claim's index and left this one, which its bench did not
reproduce. The two-machine stress run after it, 20261030-190623 on e94c4dc65,
read `sys.dm_db_index_operational_stats` before and after: this index took
52 183 ms of page latch over 105 240 inserts, about 0.50 ms a row and almost
all of `NotificationLog`'s, against 1 067 ms on the rekeyed claim index and
17 ms on the clustered key. At 1600 users the outcome's `UPDATE` waited 7.0 s
on Buffer Latch over 10 499 runs. Every outcome — sent, suppressed, opted
out, undeliverable or dispatched — sets `CompletedAt` to now, the newest key
the index holds, so each one appends its row to the index's last page, the
mechanism ADR-175 found on the claim index. Keyed on the id, whose order SQL
Server reads as random, the appends spread over the index's pages. `Status`
is set in the same write as `CompletedAt` and never again, so including it
costs no later update and lets the purge read only this index.
**Consequences.** The purge sorts every ended row's `CompletedAt` for each
batch, where an ordered scan of the old key could stop once a batch filled; a
pass runs up to `NotificationsRetentionService.MaxBatchesPerPass` such scans,
hourly, over a narrow index the length of the log's retention window. The old
key already read the whole index on the batch that came back short, the last
of any pass that drains its backlog, since the age test is `COALESCE` over
the push deliveries' ends and no seek bounds it, so the cost grows by the
batch count rather than in kind. The migration drops the index and builds
the new one offline in its one transaction, so the table's writes wait for a
build over every ended row the window keeps; a deployment whose log holds
years sizes that pause before it rolls, where ADR-175's rebuild covered only
the pending rows. Nothing here was benched:
ADR-175's bench put 84 to 1 969 ms a run on this index, too little to compare,
so the figures above are the stress run's from before the change, and
whether the latch leaves is for
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
next two-machine run to say. A sharded key, a persisted bucket of the id
leading `CompletedAt`, was refused: it keeps sixteen hot pages where this
keeps none, adds a column to the table, and needs the purge rewritten as a
seek per bucket to keep the ordered scan it would be added for.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
