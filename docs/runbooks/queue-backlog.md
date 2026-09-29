# Runbook — queue backlog and delivery lag

| | |
|---|---|
| Alerts | `QueueBacklogGrowing` and `DeliveryLag`, in `deploy/observability/alerts/platform-alerts.yaml` — one condition seen from the broker's side and from the consumer's |
| Condition | A working queue above 1000 messages and rising over 10 minutes; or event delivery p95 above 2 s over 10 minutes |
| Signal | `rabbitmq_queue_messages` from the broker's exporter; `messaging.delivery.lag`, recorded by `IntegrationEventConsumer<T>` ([§13.2](../backend-architecture/13-observability.md)) |
| Owner | The service team of the consumer ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Messages are arriving faster than something consumes them. **Nothing has
failed**: every message is still on the broker and will be delivered, so this
is work running late rather than work lost. That is why both alerts are tickets
and neither is a page — a business process that has actually stopped raises
[`error-queue.md`](error-queue.md) or [`outbox-broker.md`](outbox-broker.md)
instead, and if one of those is firing too, work it first.

What users see is staleness: a projection behind its source, an order whose
saga has not moved, a shipment whose despatch has not reached the buyer's
timeline yet.

## First, decide which end fired

The two alerts are grouped on different labels and that is the branch.

- `QueueBacklogGrowing` carries `queue`, which names one receive endpoint.
  Somebody's consumer is behind, and the queue says whose.
- `DeliveryLag` carries `service_name`, which names the host doing the
  consuming. It is measured from the message's own `OccurredAt` to the moment
  `Consume` starts, so it includes the publisher's outbox wait and the
  broker's, not only the consumer's.

Both at once is the ordinary shape of one backlog. `DeliveryLag` alone, with
every queue shallow, points at the **publisher's** outbox rather than at a
consumer — check [`outbox-broker.md`](outbox-broker.md) before touching the
consumer's replicas.

```promql
sum by (queue) (rabbitmq_queue_messages{queue!~".+_(error|skipped)"})

histogram_quantile(
  0.95,
  sum by (service_name, le) (rate(messaging_delivery_lag_seconds_bucket[10m]))
)
```

## Then decide whether it is arrival or service

A backlog is a rate problem and has exactly two sides.

- **Arrival went up.** A campaign, a replay, a backfill, another service
  catching up after its own outage. Compare the queue's inbound rate with the
  same hour yesterday before concluding anything about the consumer.
- **Service went down.** A consumer retrying inside its own endpoint, a
  database that has slowed, a third party the handler waits on, or simply too
  few replicas.

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
`autoscaling.enabled: false` with three replicas, because CPU does not move
while a queue does; this alert is how that number is found to be too small, and
the fix is `replicaCount` in `deploy/helm/shipping/values.yaml` rather than a
`kubectl scale` that the next deploy undoes.

## What these two do not cover

**Delivery lag stops when a consumer starts.** It is recorded at the top of
`Consume`, before the handler runs, so it never sees what the handler then
waited on — a carrier, an address owner, a database. A service whose work is
done in a `BackgroundService` rather than in a consumer is outside both alerts
entirely: Shipping's fulfilment and tracking workers are that shape, and their
own signal is `shipping.shipments.waiting`, the gauge of shipments past their
first failed pass, by state.

So a Shipping backlog can be invisible here while every shipment sits `Pending`
behind an unreachable carrier. Read that gauge, and
`shipping.carrier.unavailable` beside it, before concluding from a quiet queue
that the service is healthy.
[§13.7](../backend-architecture/13-observability.md) records the broker-fed
read-model gap on the same terms, and this is its sibling on the worker side.

## Closing it

Both clear on their own once the rate recovers: the depth drops below a
thousand or stops rising, and p95 falls under two seconds. Before closing,
check the backlog **drained** rather than the producer stopping — a queue
nobody publishes to has an excellent depth. The consumers' own delivery count
tells the two apart, because a drained queue is still being fed:

```promql
sum by (service_name) (rate(messaging_delivery_lag_seconds_count[10m]))
```

If that has collapsed too, the incident is upstream and is not over.
