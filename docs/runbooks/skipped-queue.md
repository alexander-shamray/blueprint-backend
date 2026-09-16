# Runbook — skipped queue depth

| | |
|---|---|
| Alert | `SkippedQueueDepth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any message in any `*_skipped` queue |
| Signal | `rabbitmq_queue_messages`, from the RabbitMQ exporter — not a solution instrument, and **per-queue series need `rabbitmq_prometheus` with per-object metrics enabled and scraped**, which §14.1's image does not configure |
| Owner | The service team that owns the endpoint ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

MassTransit handed an endpoint a message it has **no consumer for** and parked
it in `<queue>_skipped`. Nothing threw, nothing was retried, and nothing will
retry it.

**This is not the error queue's problem in a different queue.** An `_error`
message is one a consumer accepted and could not finish; a `_skipped` message
is one no consumer was willing to take. The first is a failure and the second
is a routing mismatch, and they are triaged from opposite ends — which is why
they are two alerts and two procedures rather than one selector matching both.

The threshold is `> 0` for the same reason the error queue's is: there is no
healthy number. A correctly ordered release never produces one.

## Which queue, and therefore which endpoint

The queue name is the endpoint name plus `_skipped`, so it names the receive
endpoint directly — the same mapping
[`error-queue.md`](error-queue.md) uses, and the same list of Ordering
endpoints applies. Read them from `Endpoints`/`DependencyInjection.cs` rather
than from memory.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages | grep _skipped
```

## Read the message before deciding anything

The message **type** is the whole diagnosis here, where the fault headers are
in [`error-queue.md`](error-queue.md). Getting at it is that runbook's
procedure with two words changed, and for its reasons: `rabbitmqctl` returns
queue metadata rather than bodies, the credentials are not `guest/guest`, and
they stay out of `argv`.

**`rabbitmqadmin` is not on the image this repository ships.** The broker
builds from `rabbitmq:4.1-management-alpine` with the delayed-exchange and
shovel plugins and nothing else — no Python, so the v1 script is absent, and
the v2 binary is a separate download. A procedure that cannot be executed on
the image the repository ships is not a procedure, which that Dockerfile says
about itself.

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

umask 077
cat > "$HOME/.rabbit.curl" <<'EOF'
user = "OPERATOR:PASSWORD"
EOF

curl -sS --config "$HOME/.rabbit.curl" -X POST   -H 'content-type: application/json'   -d @- http://localhost:15672/api/queues/%2F/<endpoint>_skipped/get <<'EOF'
{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}
EOF
```

`ack_requeue_true` is load-bearing for the same reason it is one runbook over:
**`ack_requeue_false` consumes the message and it is gone.** Delete the config
when the incident closes.

**The type is in the payload, not in a transport header**, and this is worth
saying because the obvious guess is wrong. This platform configures no
serializer, so MassTransit's default envelope
(`application/vnd.masstransit+json`) is in force and the type is `messageType`
**inside the body**. `MT-MessageType` belongs to the *raw* serializer and is
not stamped here — an earlier revision of this runbook sent an on-call looking
for it, and finding no such header reads as a malformed message rather than as
a wrong instruction. What the transport does stamp is `MT-Reason`, which says
why the message was moved.

Compare `messageType` against what this endpoint's consumers — and, for a
saga, the state machine's `Event<>` declarations — actually bind.

## Then it is one of three things

### A producer shipped ahead of its consumer

**The common cause, and the one this alert mainly exists for.** A release added
a binding or a new message type and reached the broker before every replica
that declares it. During a rollout both releases consume the same queue
([§15.5](../backend-architecture/15-cicd-deployment.md) makes the canary a
second release of the same chart), so the broker can hand a newly bound message
to a replica whose build has no consumer for it.

[§9.2](../backend-architecture/09-messaging.md) forbids this ordering, so
finding it means the rule was not followed rather than that the rule is
insufficient.

1. **Finish the rollout first.** Once every replica runs the new build, nothing
   further is skipped, and the queue stops growing. Do not roll back — the old
   build is the one that cannot handle these messages.
2. **Then replay what was parked**, with the shovel procedure in
   [`error-queue.md`](error-queue.md); the mechanics are identical and only the
   source queue name differs.
3. **Record it against the release.** A skipped queue after a deploy is a
   §9.2 violation and the next release of that service should not repeat it.

> **Replay is right here, and on the error queue it depends on the arrival.**
> These messages were never refused on their merits — no consumer saw them —
> so once the consumer exists they succeed. A `PaymentAuthorised` whose saga
> instance is gone is the opposite: it faults every time it is replayed,
> because a deleted instance does not come back. A faulted `OrderCancelled`
> ([#123](https://github.com/alexander-shamray/blueprint-backend/issues/123))
> sits between them — replaying it works only once the `OrderPlaced` that
> creates the instance has landed, which is why
> [`error-queue.md`](error-queue.md) reads the outbox before deciding.

### A binding the service should declare is missing

If every replica is on the same build and messages are still being skipped, the
consumer was never written. Read the type against
[§3.2](../backend-architecture/03-bounded-contexts.md)'s Consumes column: if
the table gives this service the event, the binding is owed and this is a
defect.

**Do not ship the consumer through the canary.** The producer is another
service and is publishing already, so the moment the new replica's bus starts
the queue receives that type — and the stable track, still on the old build,
skips its share for the length of the ladder. §15.5 names the way round it: a
new receive endpoint, a queue old replicas never read from. Ship it that way,
then replay what was parked.

### The message was addressed here and does not belong here

**A `Send` to the wrong endpoint, not a `Publish`** — and the distinction
matters because the obvious suspect is the wrong one. A publish reaches a queue
only if that endpoint bound the type, and it bound it *because* it has a
consumer for it; so publishing a command produces N consumers all running it
(§9.6's actual hazard) and skips nothing. A **send** is addressed to a queue
directly and arrives whatever that endpoint consumes, so a wrong destination —
a mistyped `queue:` address, a shovel, a manual reroute — is what puts a
message somewhere nothing will take it.

The tell is the destination rather than a second copy: the type in the envelope
is one this endpoint has no business receiving at all, and no other queue is
missing it.

Discard with a record once the real destination has had it. Fixing the sender
is what closes it.

## Closing it

The alert clears when the queue is empty. Confirm the messages **went
somewhere** — replayed into a consumer that now exists, or discarded with a
record of why — rather than having been purged to clear the graph. An empty
skipped queue and a green dashboard look identical whichever way it happened,
which is the trap [`outbox-abandoned.md`](outbox-abandoned.md) opens with and
the one [`error-queue.md`](error-queue.md) closes with.
