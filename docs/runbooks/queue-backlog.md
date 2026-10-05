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
too, work it first. The lag alert reads the same way with one exception: a
message whose handler fails on every attempt raises the lag and then goes to
`_error`, which is the error-queue alert's page and not this ticket.

What users see is staleness: a projection behind its source, an order whose
saga has not moved, a shipment whose despatch has not reached the buyer's
timeline yet.

The two alerts read one condition from opposite ends, for integration
events. The backlog alert carries `queue`, which names one receive endpoint,
and so says whose consumer is behind. The lag alert carries `service_name`, and
fires when events reach that service's consumers late, whether or not they have
piled up yet: a consumer that is slow at a low rate shows there long before a
thousand messages wait for it. Only event consumers record the lag, so a
backlog on a command queue or on Ordering's saga endpoint has no counterpart on
the lag side, and a quiet lag alert says nothing about them.

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
[§15.3](../backend-architecture/15-cicd-deployment.md) gives each worker's
chart `autoscaling.enabled: false`, so scaling one out is `replicaCount` in its
own `deploy/helm/<chart>/values.yaml` rather than a `kubectl scale` that the
next deploy undoes
([ADR-063](../backend-architecture/adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)).
Three things before raising it:

- **Read the processor's rate limit first.** Each replica claims its own batch
  every tick — `FulfilmentWorker.ClaimBatchSize` and
  `TrackingWorker.ClaimBatchSize` against the carrier,
  `SendWorker.ClaimBatchSize` against the relay — so replicas multiply the
  calls a carrier or a relay receives. The lease stops two replicas making one
  call twice; it does nothing for a limit. A count past the limit turns a slow
  processor into a throttled one.
- **Rule the dependency out.** A worker held to its timeout by a slow carrier,
  relay or Keycloak shows the same overdue climb, and more replicas wait on it
  faster; the sections below say how to tell.
- **Not during a rollout.** §15.5's first rung scales the stable Deployment
  for its weight and, when it ends, restores the count it found on a rollback
  and the promoted release's declared count on a promotion, so a change made
  meanwhile is either undone or skews the canary's share. Ship the new
  count with the next deploy, or after the rollout finishes.

## What this does not cover

**It sees a receive endpoint and nothing past it.** A service whose work waits
in its own tables rather than on the broker is outside it. Shipping is that
shape: its consumers only write a row — a `Pending` shipment, or a
cancellation — and the carrier and address calls that make up the real work
are done by the fulfilment and tracking workers, so Shipping's queue stays
shallow however far behind those workers fall.

Their signals are `shipping.shipments.waiting`, the gauge of shipments past
their first failed pass by state, `shipping.carrier.unavailable` beside it,
and `shipping.shipments.overdue`, how long the longest-due row each pass would
claim has waited for one, by `pass`. Before concluding from a quiet queue that
Shipping is healthy, read all three.

**The overdue gauge is the one that sees a row no pass has reached yet**, which
the other two cannot. Healthy, each claim takes a due row within a tick of its
worker's loop, so the age stays near zero. One that climbs and keeps climbing
is a pass that cannot keep up with its population. On `tracking`, a carrier
slow enough to hold every pass to its request timeout does the same, so read
`shipping.carrier.unavailable` and the carrier's latency before scaling. With
the carrier healthy, the answer is `replicaCount`, as the paragraph on a
worker's replica count above says.

**Notifications is the same shape, and its lag ends even earlier.** Its seven
consumers write a `Pending` row in `NotificationLog` — and, for Ordering's
three events, the order record — and acknowledge. Everything that leaves the
service, the contact read and the send, is the send worker's. So
`messaging.delivery.lag` for `Notifications.Worker` stops when a consumer
starts and never sees the worker's wait on Keycloak or on the relay, and
`notifications-events` stays shallow while every notification waits behind a
relay that is down: its breaker parks the queue of rows rather than the
messages, so nothing piles up on the broker and nothing reaches `_error`.

Its signal is `notifications.waiting`, the gauge of `Pending` rows past their
first backoff, by the `step` they wait on. Read the step before anything else:

- **`order_record`** — the event reached Notifications before its order's
  `OrderPlaced`, which §9.4 does not order, or a decline is waiting for the
  cancellation ADR-049 makes it read. Look upstream first: Ordering's outbox,
  and whether the Ordering events are arriving on `notifications-events` at
  all.
- **`contact`** — Keycloak is unreachable or is refusing this host's grant.
  `notifications.contact.refused` rising says it is refusing, which is a
  credential to fix, not an outage to wait out, and
  [`contact-refused.md`](contact-refused.md)'s alert pages on it. With
  Keycloak healthy, the step also counts a row this version cannot render:
  the worker's error line "stores parameters this version cannot read" names
  it, and it waits for a replica that can read it or for
  `DeliveryOptions.GiveUpAge`.
- **`relay`** — the relay is down or refusing. `notifications.mail.unavailable`
  by `cause` tells an outage (`transient`, `unconfirmed`) from somebody's
  decision (`tls`, `credential`, `rejected`), which backs off and waits for a
  fix rather than clearing on its own.

```promql
max by (step) (notifications_waiting)

sum by (cause) (rate(notifications_mail_unavailable_total[10m]))

sum(rate(notifications_contact_refused_total[10m]))
```

`max` and not `sum`, because every replica reads the same table and reports
the same rows. **Waiting has an end**: a row `Pending` past
`DeliveryOptions.GiveUpAge` becomes `Undeliverable: gave_up`, so a waiting set
that falls without the dependency recovering is notifications given up rather
than sent, and the rows' `Reason` says which. A rising `order_record` alone
during an Ordering backlog is the one step that clears itself when upstream
does.

**The overdue gauge is the one that says three replicas are too few.**
`notifications.overdue` is how long rows due for a pass have gone unclaimed
past two ticks, in `shipping.shipments.overdue`'s form, and it sees the row no
pass has reached yet, which the waiting gauge cannot: that one counts only
rows a pass has already backed off. Healthy, it stays near zero. One that
climbs while the relay and Keycloak both answer promptly is a send worker that
cannot keep up with its population, and the answer is `replicaCount`, as the
paragraph on a worker's replica count above says. One that climbs with either
slow is a dependency holding every pass to its budget, so read those first:
for Keycloak, the `contact` step above and the worker's outbound HTTP request
duration by `server_address`. `notifications.contact.refused` counts a refusal
and never a slow answer, so a flat one does not clear Keycloak. The relay is
not HTTP and no instrument times a submission: `notifications.mail.unavailable`
counts a fault and never a slow answer either, so a relay that answers slowly
inside `MailHop`'s budget shows in none of these, and is ruled out at the relay
before `replicaCount` is raised.

```promql
max(notifications_overdue_seconds)
```

**The lag alert is measured at consumer start, and a failure can raise it.**
`messaging.delivery.lag` is recorded at the top of `Consume`, before a handler
runs, so a message's own handling time is outside its lag. A slow handler
shows through the messages queued behind it, which start late. A failing one
shows too: the endpoint's in-memory retry re-enters `Consume` for every
attempt, and each attempt records the message again, measured from its
original `OccurredAt`. So before scaling out on the lag, read the service's
consume error rate — the series is absent until the service's first fault, so
no data here means none:

```promql
sum by (service_name) (rate(messaging_masstransit_consume_errors_ea_total[10m]))
```

A lag that rose with the errors is a fault to find, not a shortage of
consumers. Only a failure that is never retried leaves the lag quiet.

The lag also compares a timestamp made on another machine, so a clock skewed
between two hosts moves it without anything being late — check the
publisher's and the consumer's clocks before scaling anything on the lag
alone.

## Closing it

The backlog alert clears on its own once the depth drops below a thousand or
stops rising, and the lag alert once the p95 is back inside its target.
Before closing, check the backlog **drained** rather than the producer
stopping — a queue nobody publishes to has an excellent depth. The owning
service's finished-message rate from the step above tells the two apart,
because a drained queue is still being fed: if that has collapsed too, the
incident is upstream and is not over.
