# Runbook — orders stopped

| | |
|---|---|
| Alert | `BusinessVolumeDrop`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `orders.placed` per hour down > 50% against the same hour last week |
| Signal | `OrderMetrics`, recorded by `OrderSummaryProjection` ([§13.3](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Orders have stopped, or nearly. **This is the most valuable alert in §13.6**
because it catches a category of failure no technical metric detects: every
service healthy, no 5xx, no lag, no exception — and no orders.

Week-over-week rather than a fixed floor, because a volume alert with no
seasonality model is the first pager people mute. Tuesday 03:00 is quiet and
that is not an incident.

## Rule out "it is real" last, not first

The temptation is to assume a marketing change or a quiet day. Check the
technical causes first — they are cheap to eliminate and expensive to miss.

## 1. Can customers reach the platform at all?

```promql
sum by (service_name) (rate(http_server_request_duration_seconds_count[5m]))
```

- **Traffic collapsed too** — this is an availability incident wearing a
  business costume. Check DNS, the ingress, and the gateway's own health.
  Nothing downstream matters until requests arrive.
- **Traffic normal, orders down** — the funnel is breaking *inside* the
  platform. Continue.

## 2. Is it authentication?

An identity provider that stops issuing tokens produces a platform full of 401s
and no orders, with every service reporting perfect health.

```promql
sum by (service_name) (
  rate(http_server_request_duration_seconds_count{http_response_status_code="401"}[5m])
)
```

A spike in 401s points at Keycloak, a rotated signing key, or a realm change.
§11.4's per-service re-validation means every service rejects independently, so
this shows up everywhere at once.

## 3. The 422 that is the worked case

**This is the failure §6.6 and §13.6 both single out, and it is the reason the
alert exists.** `ordering.ProductPrices` has no row for a product; every order
containing that product is **refused by the domain**; the customer gets a 422
`order.products_unavailable`. No exception, no 5xx, no lag — nothing else in the
system registers a problem.

```promql
sum by (service_name, http_route) (
  rate(http_server_request_duration_seconds_count{http_response_status_code="422"}[5m])
)
```

**Not a 400, and the difference is where you look.** §10.5 maps `Error.Rule` to
422 and `ValidationException` to 400 — the request is well-formed and the
validator passed it, so a 400 dashboard shows a path this request never took.

If 422s are up, go to the projection:

```sql
SELECT
    Prices     = COUNT(*),
    LastUpdate = MAX(UpdatedAt)
FROM ordering.ProductPrices;
```

A stale `LastUpdate` means Catalog's events stopped arriving. That is
[`outbox-broker.md`](outbox-broker.md) on the **Catalog** side, or the
`ordering-catalog-events` endpoint on this side — and note §13.7's recorded gap:
broker-fed read-model staleness has **no direct signal**, so nothing else will
have told you.

An empty table is worse and means the projection never ran. §6.6 records that
this projection has no rebuild path of its own: Ordering holds no source of
truth for prices, so recovery is Catalog republishing — carrying each product's
**original** `OccurredAt`, because a fresh one re-lists everything ever
discontinued.

## 4. Is the outbox moving?

An order that was placed successfully but whose events never left still counts
as placed, so this will not suppress the metric — but a stalled broker lane
often accompanies whatever else is wrong. Check
[`outbox-broker.md`](outbox-broker.md) if the graphs suggest it.

## 5. The client

If the API is answering and the domain is accepting, the funnel is breaking in
front of us: a web or mobile release, a CDN, a third-party payment widget. This
platform's metrics cannot see any of it, and saying so early saves an hour of
looking at healthy dashboards.

## 6. Then, and only then, it is real

A campaign ended, a partner switched off, a competitor undercut. Record it and
consider whether the threshold should learn from it — but do not reach for this
conclusion until the five checks above are clean, because it is the one
explanation that requires no action and is therefore the most attractive.

## Closing it

The alert clears when volume recovers against last week. If the cause was a
refused-order path, confirm the **422 rate** came down and not merely the order
rate came up — a quiet hour hides an unfixed funnel just as well as a fix does.
