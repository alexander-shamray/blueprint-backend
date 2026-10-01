# Runbook — local lane stalled

| | |
|---|---|
| Alert | `OutboxLocalLaneStalled`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `outbox.oldest.age{lane="Local"}` > 30 seconds |
| Signal | `OutboxMetrics`, `Ordering.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Read models this service feeds **from its own domain events** are stale. Users
are seeing missing or outdated list data while the write side is perfectly
healthy — an order that was placed successfully does not appear in a listing.

Thirty seconds is the threshold because the local lane is in-process: there is
no network hop, `ProjectionInvoker` runs in the dispatcher's own scope, and
anything past a few seconds already means something is wrong.

## Which read models this does *and does not* cover

This is the distinction that sends people looking in the wrong service, so check
it before anything else.

- **Covered**: projections fed by this service's own domain events on the local
  lane — §6.6's `OrderSummaries` shape.
- **Not covered**: read models fed by *another* service's contract.
  `ordering.ProductPrices` is the worked case — it is written by
  `ProductPriceProjection` from Catalog's integration events, which arrive over
  the broker at `ordering-catalog-events` and **never touch this lane**.
  `ordering.Products` **will be** the second, and is not built yet — §6.6
  specifies it and `OrderSummaryProjection` does not write it, so the
  table is not in the database today. Do not go looking for it on this page's
  account: an operator querying it now gets an invalid object name, which is
  the deployment being incomplete rather than the projection being stale.
  Recorded here so the diagnostic is written before the first incident rather
  than after it. **When it lands, the two failures will look different and
  telling them apart is the whole of the triage.** A *missing* row — this
  projection has not applied a `ProductPublished` for that product — drops it
  from the response, so the order shows fewer products than it has. Missing,
  not empty: `Name` is `NOT NULL` and the handler writes every column in one
  statement, so there is no half-filled row for an operator to find. A *stale*
  row — a rename Catalog published and this table has not applied — returns
  the previous name, which is the wrong one and looks entirely healthy.
  [§13.7](../backend-architecture/13-observability.md) records that their
  staleness has no direct signal yet, and that gap is real: if prices are stale,
  this alert will not fire and neither will anything else.

For a stale `ProductPrices`, work [`outbox-broker.md`](outbox-broker.md) on the
*Catalog* side instead, and read §13.7's paragraph on why
`messaging.delivery.lag` does not cover it.

## Find the throwing handler

A local-lane stall is nearly always one projection handler throwing, and the
dispatcher's own log names it.

```bash
kubectl -n <ns> logs deploy/ordering --since=15m | grep "Outbox message .* failed"
```

Then read the row's own record of it — `LastError` is the first 2000 characters
of the exception:

```sql
SELECT TOP 10
    Id,
    MessageId,
    MessageType,
    Attempts,
    LockedUntil,
    LastError = LEFT(LastError, 2000)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL
    AND Lane = 'Local'
ORDER BY OccurredAt;
```

The three causes, in the order they occur:

- **Schema drift after a migration.** The handler writes a column the migration
  renamed or dropped. `SqlException` names it. This is the case where rolling
  the *code* forward is right and rolling the schema back is wrong.
- **A read-model deadlock.** §6.6's `MERGE` takes `HOLDLOCK`; two writers on
  overlapping ranges under load can still deadlock, and SQL Server picks a
  victim. Deadlocks are retried by the dispatcher's backoff, so a *transient*
  one clears itself — a persistent one is a range the projection is contending
  with itself over.
- **A genuine bug in the handler.** A null the projection did not expect, or a
  payload shape that changed. The `MessageType` column plus §9.4's
  `MessageTypeMap` tells you which type to read.

## A row that is stuck but not throwing

If `LastError` is null and `Attempts` is 0 while the age climbs, the dispatcher
is not getting to the row at all:

```sql
SELECT
    Lane,
    Pending   = COUNT(*),
    Oldest    = MIN(OccurredAt),
    NextLease = MIN(LockedUntil)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL
GROUP BY Lane;
```

A `LockedUntil` far in the future on a pod that has since restarted is a stale
lease — the claim leases for 60 seconds, so it resolves itself within a minute.
Anything longer than that is worth escalating rather than waiting out.

## Serving from the write model meanwhile

**A judgement call, and it is usually no.** §6.6's whole argument is that a
projection is a second copy of the truth with its own rebuild procedure;
temporarily pointing a query handler at the aggregate means a second read path
that has not been tested and will outlive the incident. Prefer to fix the
handler.

Where the staleness is customer-visible and the fix is not minutes away, say so
explicitly in the incident channel rather than shipping a quiet fallback.

## Replaying a projection from scratch

Local-lane rows are still in the outbox until the retention purge removes them
(§9.4, seven days by default), so a rebuild inside that window needs no
republish — reset the rows and let the dispatcher redeliver:

**`ProcessedAt` has to be cleared, and that is the whole difference between the
two operations.** Retrying a *stuck* row needs only the attempt counter and the
lease reset, because the dispatcher's claim already selects it. A **rebuild**
replays rows that were delivered successfully, and those carry a `ProcessedAt`
— so a predicate of `ProcessedAt IS NULL` selects exactly the rows that do not
need rebuilding and none of the ones that do.

```sql
-- Retrying one stuck row: it is already claimable, so only the backoff moves.
UPDATE ordering.OutboxMessages
SET
    Attempts    = 0,
    LockedUntil = NULL,
    LastError   = NULL
WHERE ProcessedAt IS NULL
    AND Lane = 'Local'
    AND MessageId = @MessageId;
```

```sql
-- Rebuilding from retained rows: ProcessedAt is what makes them eligible again.
-- Bound it — by time, by message id, by type — and read what you are about to
-- replay first. Unbounded, this redelivers every local-lane row inside the
-- retention window, in one burst, through a handler you have just changed.
UPDATE ordering.OutboxMessages
SET
    ProcessedAt = NULL,
    Attempts    = 0,
    LockedUntil = NULL,
    LastError   = NULL
WHERE Lane = 'Local'
    AND OccurredAt >= @RebuildFrom
    AND MessageType = @MessageType;
```

**Projections must be idempotent for this to be safe**, and §6.6 requires it —
the `MERGE` and its out-of-order guard are what make a replay a no-op rather
than a double count. If the handler you are replaying is not idempotent, that is
the defect, not the stall.

Past the retention window there is no replay from the outbox, and the rebuild is
whatever §6.6 records for that projection.

## Closing it

Watch the gauge, not the log:

```promql
max by (service_name) (outbox_oldest_age_seconds{lane="Local"})
```

It should fall to near zero. If it falls and the pending count stays high, the
oldest row is moving but the lane is not keeping up — that is
[`outbox-growth.md`](outbox-growth.md).
