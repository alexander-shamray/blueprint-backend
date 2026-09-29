# Runbooks

One per alert, both ways.

[§13.9](../backend-architecture/13-observability.md) is the specification:
*"Every alert links to a runbook. An alert that fires at 03:00 with no procedure
attached is a page to somebody who will have to reason from scratch."* The
pairing is checked in both directions by
[`deploy/observability/check.py`](../../deploy/observability/check.py) — an
alert with no runbook and a runbook with no alert both fail the build.

**This file is not one of them.** `check.py` excludes exactly `README.md` and
declares that exclusion, so a second non-runbook file here has to be argued for
rather than added.

| Runbook | Alert | Loaded? |
|---|---|---|
| [`error-rate.md`](error-rate.md) | `ErrorRateGateway`, `ErrorRateService` | yes |
| [`latency.md`](latency.md) | `Latency` | yes |
| [`outbox-broker.md`](outbox-broker.md) | `OutboxBrokerLaneStalled` | yes |
| [`projection-lag.md`](projection-lag.md) | `OutboxLocalLaneStalled` | yes |
| [`outbox-growth.md`](outbox-growth.md) | `OutboxGrowth` | yes |
| [`outbox-abandoned.md`](outbox-abandoned.md) | `OutboxAbandonedRows` | yes |
| [`error-queue.md`](error-queue.md) | `ErrorQueueDepth` | yes |
| [`skipped-queue.md`](skipped-queue.md) | `SkippedQueueDepth` | yes |
| [`queue-backlog.md`](queue-backlog.md) | `QueueBacklogGrowing` | yes |
| [`migration-failure.md`](migration-failure.md) | `MigrationJobFailed` | yes |
| [`stuck-saga.md`](stuck-saga.md) | `StuckSaga` | **no — signal owed** |
| [`order-review.md`](order-review.md) | `OrdersAwaitingReview` | **no — signal owed** |
| [`redis-cold.md`](redis-cold.md) | `CacheHitRatioCollapse` | **no — signal owed** |
| [`business-volume.md`](business-volume.md) | `BusinessVolumeDrop` | **no — signal owed** |

**Four alerts cannot fire yet**, because nothing publishes the instrument they
read. Their rules sit in `deploy/observability/alerts/awaiting-signal.yaml`,
unloaded, and each runbook opens by saying so. The procedures still work —
every query in them reads a table or a log that exists today — which is what
§13.9 means by *"write each one when the corresponding alert is created, not
after it first fires"*.

## Writing a new one

- **Open with what it means for a user**, not with what the metric did. The
  first paragraph is read at 03:00 by somebody deciding how alarmed to be.
- **Say what is *not* affected.** A broker-lane stall is a delivery failure and
  not a data-loss one, and knowing that is the difference between a calm hour
  and a panicked rollback.
- **Distinguish the fault from its lookalike.** Nearly every runbook here has
  one pair that fails identically on the graph and needs opposite responses.
- **Give the query, with the real table and column names.** A procedure that
  says "check the outbox" has not saved anybody anything.
- **Say how to close it**, and how to tell a fix from a quiet hour. Several of
  these alerts clear when traffic stops, which is not the same as recovery.

## Conventions

British spelling, prose wrapped at 80 columns, `§n.n` for blueprint
cross-references — the repository's conventions, and these files are inside the
one rule that says the blueprint and the code must not contradict each other.
Where a runbook and a chapter disagree, **the chapter wins**, exactly as §12
wins over `docs/testing.md`.
