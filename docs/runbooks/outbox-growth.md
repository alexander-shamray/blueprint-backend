# Runbook — outbox backlog growing

| | |
|---|---|
| Alert | `OutboxGrowth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | pending rows > 1000 **and rising** over 10 minutes, replicas deduplicated |
| Signal | `OutboxMetrics`, the service's `Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## Whose service

The alert fires for whichever service exports the gauge, and every example
below is Ordering's. [`outbox-broker.md`](outbox-broker.md#whose-service) says
how to read one for another service.

## What it means

Either lane is not keeping up. This is a *ticket* rather than a page, and the
severity is doing real work: the backlog is draining more slowly than it fills,
which is a capacity problem hours away from being an outage, not an outage.

**Both halves of the condition matter.** A backlog of ten thousand rows that is
falling needs nobody — it is a recovery in progress after
[`outbox-broker.md`](outbox-broker.md), and paging on the count alone would page
during every successful recovery. The `deriv(...) > 0` half is what keeps this
alert about growth.

## Why this is not the age alert

`outbox.oldest.age` catches a lane that has **stopped**; this catches one that
is **falling behind**. Neither substitutes for the other, which is why §13.6
ships both: a single stuck row pins the age gauge at hours while the count stays
at one, and a backlog of ten thousand rows all seconds old leaves the age gauge
flat.

So check the age gauge first — if it is also high, this is a stall and you want
[`outbox-broker.md`](outbox-broker.md) or
[`projection-lag.md`](projection-lag.md) instead.

```promql
max by (service_name, lane) (outbox_oldest_age_seconds)
max by (service_name, lane) (outbox_pending_count)
```

The `lane` label says which side is growing, and they have different answers.

**`max` per lane, then sum the lanes — never `sum` over the raw series.** These
gauges read the database, so every replica exports the same number and Ordering
runs three; a plain sum reports three times the backlog and would trip a
thousand-row threshold at about 334 real rows. The alert aggregates the same
way, which is why its condition above is written in words rather than copied
from the rule.

## Is the dispatcher alive and claiming?

Count the dispatcher's delivery failures over the last ten minutes in Grafana's
Explore, on the Loki data source; the hosts log through OpenTelemetry alone
([§13.4](../backend-architecture/13-observability.md)), so `kubectl logs` shows
nothing. The dispatcher logs `Outbox message …` only when a delivery fails, so a
count above zero says it is running and failing, and zero says nothing either
way: a healthy dispatcher is silent, and so is a dead one.

```logql
count_over_time({service_name="<the alert's service_name>"} |~ "Outbox message" [10m])
```

```bash
kubectl -n <ns> get pods -l app.kubernetes.io/name=<workload>
```

A dispatcher that is not running produces a backlog that rises in a straight
line with no errors at all. `AddHostedService<OutboxDispatcher>()` is registered
in `Add<Prefix>Infrastructure`; nothing resolves it at startup, so a lost
registration is silent and the service still reports ready.

## Batch size against arrival rate

The claim takes `OutboxDispatcher.ClaimBatchSize` rows a pass, and while
claims come back full and delivering, the next pass starts at once rather than
at the next `PollInterval` tick
([ADR-163](../backend-architecture/adr/ADR-163-the-outbox-dispatcher-publishes-broker-rows-side-by-side-and-claims-again-at-once-after-a-full-claim-1535-adding-to-adr-131.md)).
So under a backlog one replica's ceiling is set by delivery time, not by the
tick: `Local` rows go one at a time and `Broker` rows up to
`OutboxDispatcher.BrokerConcurrency` at once. The drain rate is the
dispatcher's own delivered count
([§9.4](../backend-architecture/09-messaging.md)):

```promql
sum by (service_name, lane) (rate(outbox_delivery_duration_seconds_count{outcome="delivered"}[5m]))
```

The pending count's slope is arrivals less that rate, never either one on its
own, so a rising slope beside a steady drain rate is arrivals outrunning it:

```promql
deriv(sum by (service_name) (max by (service_name, lane) (outbox_pending_count))[10m:])
```

If arrivals genuinely exceed the drain rate, the fixes in order of preference
are: add replicas (the claim uses `UPDLOCK, READPAST` precisely so replicas do
not collide); then raise the batch size or the Broker bound; and only then
question the tick, which paces only a lane that is not backlogged. Note that
§9.4 treats the batch size, the bound and the tick as **constants rather than
configuration** ([§15.4](../backend-architecture/15-cicd-deployment.md)
argues why), so changing any of them is a code change and a pull request, not
a values file.

## A slow deliverer rather than a large batch

More often the batch is fine and each delivery is slow. Look at where the time
goes:

- **Broker lane** — publish latency to RabbitMQ. A broker under memory pressure
  throttles publishers rather than refusing them, which reads as slowness.
- **Local lane** — the projection handler's own SQL. `projection.lag` is the
  instrument, and a handler doing a row-at-a-time write inside a `MERGE` loop is
  the usual finding.

```promql
histogram_quantile(0.95, sum by (le) (rate(projection_lag_seconds_bucket[10m])))
```

## Has the retention purge failed?

This is the cause people miss, because it produces a large count with a *low*
age. `RetentionPurgeService` deletes processed outbox rows past the window
(§9.4, §9.5). If it has stopped, the table grows without any delivery problem at
all — and `outbox.pending.count` counts only unprocessed rows, so a purge
failure alone should **not** move this gauge.

That makes the check diagnostic rather than corrective: if pending is high but
the table is enormous, you have two problems.

```sql
SELECT
    Pending   = SUM(CASE WHEN ProcessedAt IS NULL THEN 1 ELSE 0 END),
    Processed = SUM(CASE WHEN ProcessedAt IS NOT NULL THEN 1 ELSE 0 END),
    Oldest    = MIN(OccurredAt)
FROM <schema>.OutboxMessages;
```

A large `Processed` count is a purge that is not running — check the hosted
service's logs, and remember it is registered last and therefore stopped first
on every deploy, so a crash-looping pod may never reach a purge pass.

## Mitigation

Add replicas. It is the one lever that needs no code change, and the claim
statement was written to make it safe.

Do **not** delete unprocessed rows to bring the number down. They are the only
copy of those events, and §9.4's retention predicate refuses to delete them for
exactly that reason.

## Closing it

The alert clears when the count drops below 1000 or stops rising. Confirm the
*derivative* went negative rather than the count merely plateauing at a high
level — a flat backlog of 5000 rows is not resolved, it is a queue nobody is
draining.
