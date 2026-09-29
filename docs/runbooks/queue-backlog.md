# Runbook — queue backlog

| | |
|---|---|
| Alert | `QueueBacklogGrowing` and `DeliveryLagHigh`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | A working queue above 1000 messages and rising over 10 minutes; or a service's event delivery p95 past [§13.7](../backend-architecture/13-observability.md)'s target over 10 minutes |
| Signal | `rabbitmq_queue_messages` from the broker's exporter; `messaging.delivery.lag` from the consumer |
| Owner | The service team of the consumer ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Messages are arriving at a receive endpoint faster than its consumer takes
them. **Nothing has failed**: every message is still on the broker and will be
delivered, so this is work running late rather than work lost. That is why it
is a ticket and not a page — a business process that has actually stopped
raises [`error-queue.md`](error-queue.md) or
[`outbox-broker.md`](outbox-broker.md) instead, and if one of those is firing
too, work it first.

What users see is staleness: a projection behind its source, an order whose
saga has not moved, a shipment whose despatch has not reached the buyer's
timeline yet.

The two alerts are one condition read from opposite ends. The backlog alert
carries `queue`, which names one receive endpoint, and so says whose consumer
is behind. The lag alert carries `service_name`, and fires when messages reach
that service's consumers late, whether or not they have piled up yet: a
consumer that is slow at a low rate shows here long before a thousand messages
wait for it.

```promql
sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"})

histogram_quantile(
  0.95,
  sum by (service_name, le) (rate(messaging_delivery_lag_seconds_bucket[10m]))
)
```

## First, decide whether it is arrival or service

A backlog is a rate problem and has exactly two sides.

- **Arrival went up.** A campaign, a replay, a backfill, another service
  catching up after its own outage. The consumers are taking messages at
  their usual rate or faster, and there are simply more of them.
- **Service went down.** A consumer retrying inside its own endpoint, a
  database that has slowed, a third party the handler waits on, or simply too
  few replicas. The consumers are taking messages more slowly than usual.

Depth rises either way, so it cannot tell them apart. The rate at which the
owning service finishes messages can: compare it with the same hour yesterday
before concluding anything about the consumer. MassTransit counts every
consumer type, commands and events alike, and errors are subtracted because a
retried message is consumed again without draining anything. The errors
series does not exist until a service's first fault, so it is coalesced to a
zero per service; without that, `-` matches nothing and a healthy service
reads as no data:

```promql
sum by (service_name) (rate(messaging_masstransit_consume_ea_total[10m]))
-
(
  sum by (service_name) (rate(messaging_masstransit_consume_errors_ea_total[10m]))
  or
  0 * sum by (service_name) (rate(messaging_masstransit_consume_ea_total[10m]))
)
```

It is per service, not per queue, so read the service that owns the queue the
alert names; a service with two busy queues blends them. Ordering's saga
endpoint is counted by `messaging_masstransit_saga_ea_total` and
`messaging_masstransit_saga_errors_ea_total` instead, in the same form.

```bash
kubectl -n <ns> get deploy <workload> -o jsonpath='{.spec.replicas}{"\n"}'
kubectl -n <ns> logs deploy/<workload> --since=15m | grep -i retry | head
```

**The lookalike is a consumer that is not running at all.** A scaled-to-zero
Deployment or a crash loop produces a queue that grows and never drains, and
adding replicas to it adds pods that fail the same way. Check the endpoint has
consumers before scaling a workload that has none. A consumer that is running
but was never registered for a message its endpoint receives is a different
fault again, and raises [`skipped-queue.md`](skipped-queue.md) rather than
this.

## Mitigation before diagnosis

In order of preference: scale the consuming workload out; pause the producer if
it is a backfill somebody started; and only then consider whether the handler
itself is the problem. Scaling out is safe for every consumer in this platform
— §9.5's inbox filter makes a repeated delivery a no-op and §9.4's dispatcher
claims rows under a lease, so two replicas of a consumer do not double anything.

**A worker's replica count is a chart value and not an autoscaler's.**
[§15.3](../backend-architecture/15-cicd-deployment.md) gives Shipping
`autoscaling.enabled: false`, so scaling it out is `replicaCount` in
`deploy/helm/shipping/values.yaml` rather than a `kubectl scale` that the next
deploy undoes.

## What this does not cover

**It sees a receive endpoint and nothing past it.** A service whose work waits
in its own tables rather than on the broker is outside it. Shipping is that
shape: its consumers only write a row — a `Pending` shipment, or a
cancellation — and the carrier and address calls that make up the real work
are done by the fulfilment and tracking workers, so Shipping's queue stays
shallow however far behind those workers fall.

Their signals are `shipping.shipments.waiting`, the gauge of shipments past
their first failed pass by state, and `shipping.carrier.unavailable` beside it.
**A row no pass has reached yet is on neither**, so nothing today says that
Shipping's replica count is too small — [§15.3](../backend-architecture/15-cicd-deployment.md)
records that as owed. Before concluding from a quiet queue that Shipping is
healthy, read those two, and count the `Pending` rows that have not yet been
attempted:

```sql
SELECT COUNT(*)
FROM shipping.Shipments
WHERE Status = 'Pending'
    AND Attempts = 0
    AND TerminalAt IS NULL;
```

**The lag alert stops where the handler starts.** `messaging.delivery.lag` is
recorded at the top of `Consume`, before a handler runs, so a handler that is
slow or failing after it starts leaves this quiet; [§13.7](../backend-architecture/13-observability.md)
records that gap. It also compares a timestamp made on another machine, so a
clock skewed between two hosts moves it without anything being late — check
the publisher's and the consumer's clocks before scaling anything on the lag
alone.

## Closing it

The backlog alert clears on its own once the depth drops below a thousand or
stops rising, and the lag alert once the p95 is back inside its target.
Before closing, check the backlog **drained** rather than the producer
stopping — a queue nobody publishes to has an excellent depth. The owning
service's finished-message rate from the step above tells the two apart,
because a drained queue is still being fed: if that has collapsed too, the
incident is upstream and is not over.
