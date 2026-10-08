# Runbook — broker lane stalled

| | |
|---|---|
| Alert | `OutboxBrokerLaneStalled`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `outbox.oldest.age{lane="Broker"}` > 2 minutes |
| Signal | `OutboxMetrics`, `Ordering.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | Platform — a broker-lane stall is usually a shared-broker fault ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

This service has integration events written to its outbox that it has not
managed to publish. **Other services are working from stale data**, and any saga
waiting on one of these events has stopped advancing.

The threshold is two minutes rather than the local lane's thirty seconds because
the broker lane crosses a network: it is meant to absorb a short RabbitMQ blip
or a rolling broker restart without paging anyone. Two minutes means it did not.

## What is *not* affected

Nothing user-facing, yet. §6.3 commits the aggregate and its outbox row in one
transaction, so writes are still succeeding and the data is safe — this is a
delivery failure, not a data-loss one. That changes if rows reach the attempt
cap, which is [`outbox-abandoned.md`](outbox-abandoned.md).

## Is the dispatcher running, or is the broker refusing?

These fail identically on the graph and need opposite responses.

```bash
kubectl -n <ns> logs deploy/ordering --since=10m | grep -i "Outbox claim failed\|Outbox message"
```

- **`Outbox claim failed`** — the dispatcher cannot reach *SQL Server*. This is
  a database incident wearing an outbox costume; readiness should be red too
  ([§13.5](../backend-architecture/13-observability.md)).
- **`Outbox message … failed, attempt N of 10`** — the dispatcher is running and
  delivery is failing. Continue below.
- **Silence** — the dispatcher is not running at all. Check the pod is up and
  that `AddHostedService<OutboxDispatcher>()` survived the last change to
  `AddOrderingInfrastructure`; a registration nothing resolves at startup fails
  quietly, and the service reports ready regardless.

## Check RabbitMQ, in this order

Through the Management API, with the port-forward and the curl config
[`error-queue.md`](error-queue.md) sets up, from an operator credential carrying
`monitoring`, which sees every node and connection, and `policymaker` for the
policy list below. Not `rabbitmqctl` through `kubectl exec`: that needs
`pods/exec` on the broker and authenticates with the node's Erlang cookie,
which is full control of the node with no broker account behind it.

```bash
api() {
  curl -sS --config "$HOME/.rabbit.curl" "http://localhost:15672/api/$1"
}
api 'nodes?columns=name,running,mem_alarm,disk_free_alarm'
api 'queues?columns=name,messages,consumers'
api 'connections?columns=user,state'
```

1. **Reachable?** A DNS or NetworkPolicy change is the commonest cause and the
   least obvious — the connection string is fine, the host simply is not
   routable from this pod any more.
2. **Credentials?** `ConnectionStrings__RabbitMq` is an External Secrets–managed
   Secret ([§15.4](../backend-architecture/15-cicd-deployment.md)); a rotation
   that reached the vault and not the pod presents exactly like this. Compare
   the mounted secret's version with the vault's.
3. **Queue at its length limit — and read the overflow mode before deciding
   what happened.** A queue at `max-length` does **not** block the publisher by
   default: `drop-head` discards the *oldest* messages and the publish
   succeeds, so this lane would not stall at all — it would lose messages
   silently, which is a worse incident wearing no symptoms. Only
   `reject-publish` and `reject-publish-dlx` refuse the publisher, and only
   those can produce the stall this alert fired on.

   ```bash
   api 'queues?columns=name,messages,arguments,policy'
   api 'policies'
   ```

   The limit and the mode arrive either as queue `arguments`
   (`x-max-length`, `x-overflow`) or from a `policy`, so both have to be read —
   `queues?columns=name,messages` alone shows a full queue and nothing about
   what it does when full.

   If the mode is `drop-head`, **this alert is not your problem and the missing
   messages are**: go and find out what was dropped. If it rejects, drain the
   consumer rather than raising the limit.
4. **Memory or disk alarm?** The `nodes` call reports both. An alarmed node
   blocks publishers, which looks like a hang rather than an error.
5. **The delayed-exchange plugin.** ADR-021 schedules saga timeouts on it, which
   is why §14.1's RabbitMQ image is built rather than pulled. A broker that came
   back without the plugin accepts ordinary publishes and fails scheduled ones.

## While it is stopped

Nothing needs replaying by hand. The dispatcher polls twice a second, and every
row is retried until it is delivered or hits the attempt cap; §9.5's inbox
filter makes redelivery safe on the consumer side, so a burst after recovery is
not a hazard.

**What recovery does *not* promise is order.** The claim takes `TOP (100) …
ORDER BY OccurredAt` with `READPAST`, so each replica leases its own batch and
delivers it independently — a later batch on one replica can publish before an
earlier one on another. §9.4 treats the outbox as unordered by design and
consumers are built for it. An earlier version of this file said rows "keep
their place in `OccurredAt` order", which is a guarantee the platform does not
make and one an on-call might otherwise rely on while reconciling.

**Do not delete rows to clear the backlog.** They are the only copy of those
events.

## Recovering

Once the broker is back, watch the gauge fall rather than assuming:

```promql
max by (service_name) (outbox_oldest_age_seconds{lane="Broker"})
max by (service_name) (outbox_pending_count{lane="Broker"})
```

The age gauge should drop to near zero within a minute or two of the backlog
draining. If the count falls but the **age does not**, a specific old row is
failing while newer ones succeed — that is a poison message, and it is
[`outbox-abandoned.md`](outbox-abandoned.md) once it hits the cap. Read it now
rather than waiting:

```sql
SELECT TOP 10
    Id,
    MessageId,
    MessageType,
    Attempts,
    LockedUntil,
    LastError = LEFT(LastError, 500)
FROM ordering.OutboxMessages
WHERE ProcessedAt IS NULL
    AND Lane = 'Broker'
ORDER BY OccurredAt;
```

## What downstream is missing

Worth writing into the incident record, because it is the part nobody
reconstructs afterwards. §3.2 owns the contract map; for Ordering's broker lane
the consumers are the services subscribed to `OrderPlaced`, `OrderCancelled` and
the rest of §9.6's published set. Each of them has been operating on a world
that stopped at the timestamp of the oldest unshipped row.
