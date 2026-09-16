# Runbook — error queue depth

| | |
|---|---|
| Alert | `ErrorQueueDepth`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any message in any `*_error` queue |
| Signal | `rabbitmq_queue_messages`, from the RabbitMQ exporter — not a solution instrument |
| Owner | The service team that owns the consumer ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

MassTransit retried a message to exhaustion and moved it to the error queue.
**A business process has stopped** — whatever that message was going to cause
has not happened, and nothing will retry it without a human.

The threshold is `> 0` because there is no healthy number of poison messages.

## Which queue, and therefore which consumer

The queue name is the endpoint name plus `_error`, so it names the receive
endpoint directly. §9.8 and §9.4 define them, and **Ordering declares four** —
each with an error queue of its own:

| Endpoint | Carries |
|---|---|
| `ordering-catalog-events` | Catalog's three product events → the price projection (§6.6) |
| `ordering-commands` | The four commands §9.6's saga sends to this service |
| `ordering-stock-events` | `StockReserved` → `Order.ConfirmStock` |
| `ordering-fulfilment-saga` | The saga's own correlated events and timeouts |

**List them from `Endpoints`/`DependencyInjection.cs` rather than from memory.**
An earlier version of this runbook named the first two and stopped, which is
worse than naming none: the `_error` grep below still shows all four, but an
on-call who trusts a short inventory reads a poisoned `ConfirmStock` or a stuck
saga as "no Ordering error queue" and looks in another service.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages | grep _error
```

## Read the message before deciding anything

**`rabbitmqctl` will not do this.** `list_queues` and `info_all` return queue
metadata and counts — they do not return a message body or a header, which is
everything the steps below need. Use the Management API's `get` with
`ackmode=ack_requeue_true`, which reads the message and puts it back:

**The credentials are not `guest/guest`, and since
[ADR-036](../backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)
that account exists nowhere.** It was §14.1's Compose default until the broker
gained per-service identities; a deployed broker's credentials are supplied by
External Secrets ([§15.4](../backend-architecture/15-cicd-deployment.md)), one
Secret per service. **A service account will not do either**: this procedure
needs the Management API and the shovel, and a `*-svc` account carries no tags
at all — deliberately, since `administrator` is what made the shared account
worth stealing. Whoever runs this needs an operator credential from the vault,
not a copy of a service's.

**Keep them out of `argv`.** Anything passed as `curl -u` or `-d` is visible in
the process list to every user on the box, and an incident is exactly when a
shell is being shared and scrollback is being pasted into a channel. A
mode-0600 curl config carries the credential, and the request body comes from
stdin:

```bash
kubectl -n <ns> port-forward svc/rabbitmq 15672:15672 &

umask 077
cat > "$HOME/.rabbit.curl" <<'EOF'
user = "OPERATOR:PASSWORD"
EOF

curl -sS --config "$HOME/.rabbit.curl" -X POST \
  -H 'content-type: application/json' \
  -d @- http://localhost:15672/api/queues/%2F/<endpoint>_error/get <<'EOF'
{"count":5,"ackmode":"ack_requeue_true","encoding":"auto"}
EOF
```

**Delete it when the incident closes** — `rm -f "$HOME/.rabbit.curl"` — and use
a credential you can revoke rather than the service's own.

`ackmode=ack_requeue_true` is the load-bearing part: **`ack_requeue_false`
consumes the message and it is gone.** The Management UI's *Get messages* with
**Requeue: yes** is the same call behind a form, and is the usual way to do it.

What you want off the result:

- **`MessageId`** — ties to the outbox row that published it, and to §9.5's
  inbox.
- **`CorrelationId`** — reaches the originating request's logs and trace
  ([§10.4](../backend-architecture/10-api-gateway.md)).
- **`MT-Fault-Message` / `MT-Fault-StackTrace`** — MassTransit's own headers,
  carrying why the final attempt failed. This is the field that decides
  everything below.

## Replay or discard

### Replay — the message is fine, the world was broken

A dependency that was down, a database that was failing over, a deploy mid-roll.
The payload is valid and the consumer would now succeed.

Move it back with the Management API's shovel, declared as a one-shot
parameter. It moves every message on the error queue back to the endpoint and
deletes itself when the queue is empty:

**Percent-encode the credential before it goes in the URI.** A generated
password containing `@`, `:`, `/`, `#` or `%` — which a vault-issued one often
is — gets parsed as URI structure rather than as the credential, so the shovel
is created and then fails to connect. During an incident that reads as "the
replay did nothing".

The body carries that URI, so it goes in through stdin for the same reason the
first request did, and never on the command line:

```bash
# Reads from stdin, NOT from argv. An earlier version passed the value as an
# argument, so the encoder written to keep the password off the command line
# put it on the command line of its own child — visible in the process list for
# as long as Python ran.
enc() { python3 -c 'import sys,urllib.parse as u; print(u.quote(sys.stdin.read(), safe=""), end="")'; }
uri="amqp://$(printf '%s' "$OPERATOR" | enc):$(printf '%s' "$OPERATOR_PASSWORD" | enc)@localhost:5672/%2F"

# Unquoted heredoc, so $uri expands. Everything else here is literal.
curl -sS --config "$HOME/.rabbit.curl" -X PUT \
  -H 'content-type: application/json' \
  -d @- http://localhost:15672/api/parameters/shovel/%2F/replay-ENDPOINT <<EOF
{"value":{
  "src-protocol":"amqp091","src-uri":"$uri","src-queue":"ENDPOINT_error",
  "dest-protocol":"amqp091","dest-uri":"$uri","dest-queue":"ENDPOINT",
  "src-delete-after":"queue-length","ack-mode":"on-confirm"}}
EOF

# Watch it drain, then confirm the shovel removed itself.
curl -sS --config "$HOME/.rabbit.curl" http://localhost:15672/api/shovels/%2F
```

**Both URIs are spelled out, and a bare `amqp://` is the trap.** The shovel runs
*inside* the broker and connects with its own credentials, so an unqualified URI
means `guest` — which on a deployed broker is not a user. The API accepts the
parameter and the shovel then fails to connect at both ends, which is a worse
outcome than a rejected request because it looks like it worked. Both failure
modes present identically, and both are silent.

**The shovel definition persists with the password in it** until it deletes
itself or you remove it. `DELETE /api/parameters/shovel/%2F/replay-<endpoint>`
if it is still there after the queue drains.

`src-delete-after: queue-length` is what makes this one-shot: the shovel stops
after the messages present when it started, so a consumer that fails again does
not get replayed in a loop. `ack-mode: on-confirm` is what makes it safe — a
message is removed from the error queue only once the destination has confirmed
it.

**Requires the `rabbitmq_shovel` and `rabbitmq_shovel_management` plugins.**
§14.1's image now enables both — it was the delayed exchange alone until this
procedure needed them, because a replay path the shipped image cannot run is
not a replay path. A deployed broker is somebody else's image, so check anyway:

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmq-plugins list | grep shovel
```

If they are absent, **stop and enable them** rather than improvising:

```bash
kubectl -n <ns> exec deploy/rabbitmq -- \
  rabbitmq-plugins enable rabbitmq_shovel rabbitmq_shovel_management
```

Both ship inside the official image, so this needs no download and no restart —
which is why it is the recovery here rather than a hand-rolled consume-and-
republish loop. That loop is where messages get lost at 03:00: it has to
reproduce the headers `MT-Fault-*` and `MessageId` exactly, and a mistake
consumes the evidence.

**Replay is safe by design and it is worth knowing why.** §9.5's inbox filter
records `MessageId` and makes a second delivery of the same message a no-op
inside the handler's own transaction — so replaying something that partly
succeeded does not double it. That guarantee is per consumer, so it holds for
the endpoint this message came from and says nothing about side effects a
handler produced outside the database.

### Discard — the message can never succeed

A contract the consumer no longer understands, a payload that was malformed at
source, or an event whose effect has since been produced another way.

Purge only after recording the message body somewhere durable. Once it is gone
the only record is whatever you saved.

```bash
kubectl -n <ns> exec deploy/rabbitmq -- rabbitmqctl purge_queue <endpoint>_error
```

**`purge_queue` takes the whole queue, and the shovel above takes the whole
queue too.** Neither filters, so on a **mixed** queue there is no order of the
two that is safe: shovel first and the unreplayable messages go back to a
consumer that will refuse them again; purge first and the replayable ones are
gone.

**So do not purge a mixed queue.** Empty it one way or the other instead:

1. **Replay everything**, then work whatever returns to the error queue as a
   smaller, uniform problem. Poison messages simply come back, which costs a
   round trip and loses nothing.
2. Or **move the unreplayable ones out by hand first** — `get` with
   `ackmode=ack_requeue_false` consumes exactly the messages it returns, so
   with `count` set to the number you have identified and confirmed at the head
   of the queue, that is a selective removal. Record each body before it goes.

The first is almost always right. Reach for the second only when replaying
would cause a side effect the inbox filter does not cover — §9.5 makes
redelivery of the same `MessageId` a no-op *inside* the handler's transaction,
and says nothing about what a handler did outside the database.

### Escalate — the message is fine and so is the consumer

**Some arrivals here are neither a bug nor a broken dependency.** §9.6's saga
faults deliberately on `PaymentAuthorised`, and on an `OrderCancelled` this
service cannot account for
([#124](https://github.com/alexander-shamray/blueprint-backend/issues/124),
[#123](https://github.com/alexander-shamray/blueprint-backend/issues/123)),
rather than letting MassTransit's default consume either silently — because
the alternative is money moving, or a customer's cancellation vanishing, with
nothing raised anywhere.

**Recognise them by the same two things, because they were built to read
alike.** The queue is `ordering-fulfilment-saga_error`, and `MT-Fault-Message`
names a saga instance that could not be found rather than an exception thrown
inside a transition: the cancellation branch raises the same `SagaException`
`Fault()` raises, deliberately, so one recognition covers both. **The message
type then selects the procedure**, and it is in the body's `messageType` rather
than in a header — [`skipped-queue.md`](skipped-queue.md) says where.

#### `PaymentAuthorised` — money moved on an order nothing is tracking

Payments authorised a card for an order whose fulfilment saga had already
finished, normally one this platform cancelled. Replaying it can never work.
Then:

1. **Do not replay.** The instance is deleted and will not come back, so a
   replay faults again on the same message. This is the one case in this
   runbook where replay is wrong even though the message is well formed.
2. **Take the order id from the message and follow
   [`order-review.md`](order-review.md)'s
   `payment_authorised_during_compensation` procedure**, which is written for
   exactly this money question. The row it expects may not exist — that is the
   point of this arrival: the saga was gone before it could raise one, so the
   error-queue message is standing in for the row.
3. **Discard with a record once the money is resolved**, on the terms below.

> **A run of these is a different signal from one of them.** A single arrival
> is an interleaving §9.6 bounds but cannot prevent. Several for the same
> period means Payments is answering later than the saga's thirty-minute
> verdict bound, which is a conversation with Payments about latency rather
> than an incident in Ordering.

#### `OrderCancelled` — a cancellation with no workflow to stop

Something cancelled the order and no saga instance existed to hear it. Only
the arrivals this service can prove are its own are discarded: the saga's echo,
which carries an `Origin` of `workflow`, and an absent `Origin`, which is a
rolling deploy publishing from before the field existed. **Everything else
faults, so `user` is the EXPECTED value here rather than the only possible
one** — the branch is an allow-list, and a blank, a malformed field or a
vocabulary member some future release starts sending reaches this queue
exactly as a customer's cancellation does.

**So read `Origin` first, and branch on it before anything else.** A value
of `user` is a real cancellation and the two procedures below are for it.
**Anything else is a contract failure rather than an order to recover**:
some producer is sending an origin this build does not know, which is a
deployment problem (§9.2, ADR-026) and not something replaying the message
fixes. Find the producer, record the value, and take it to whoever owns that
release; do not run the recovery below on it, because it assumes a customer
asked and that is the one thing an unknown origin does not establish.

> **The suite says this can happen rather than the prose merely allowing
> it.**
> `A_cancellation_carrying_an_unknown_origin_faults_rather_than_being_discarded`
> publishes `operations_console` and asserts the fault, which is what an
> allow-list is worth having for — and what makes "everything here is a user
> cancellation" a claim this page cannot make.

For a `user` origin there are two ways to get here, and they want opposite
things.

**Read `Origin` and the order id off the body, then ask whether the saga ever
existed.** [`stuck-saga.md`](stuck-saga.md)'s first query answers it for a live
instance; its outbox query answers the rest, by showing whether this order's
`OrderPlaced` was ever dispatched.

1. **The cancellation overtook its own `OrderPlaced`.** §9.4 orders nothing
   between two of Ordering's own outbox rows, so a cancellation can reach the
   saga's queue before the placement that creates the instance. §9.8's retry
   envelope — about seventy seconds — normally covers that gap, which is why
   the arrival faults rather than being discarded: the retries are what give
   the placement time to land. **Reaching this queue means it did not land
   inside that envelope**, so find the `OrderPlaced` row before deciding
   anything.
   - **The `OrderPlaced` row is unsent or failing** — an outbox fault, and
     [`outbox-broker.md`](outbox-broker.md) is the procedure. Fix it, let the
     placement create the instance, and **then replay this message**: it
     correlates, compensates, and releases whatever was reserved. This is the
     one faulted arrival on this page replay can fix, and the order matters —
     replayed before the placement lands, it faults again.
   - **The `OrderPlaced` row is there and `ProcessedAt` is set, and still no
     saga exists.** The dispatcher published it and the saga never consumed
     it, which is a delivery fault rather than an outbox one: the binding or
     the consumer was absent when it arrived, so the placement went to
     `ordering-fulfilment-saga_skipped` with nothing to correlate it to.
     [`skipped-queue.md`](skipped-queue.md) is the procedure. **Restore the
     placement first and this message second** — the same ordering the
     branch above needs, for the same reason.

     > **This branch was missing and the tree read as though it were
     > complete.** `ProcessedAt` proves publication and nothing about
     > consumption, which the bullet below already says — so a placement that
     > was published, skipped, and never consumed fitted neither "unsent or
     > failing" nor "no row at all", and an operator following the tree would
     > have fallen through to the discard. **A decision tree with no branch
     > for a reachable state is worse than one that admits it stops**, because
     > the reader takes the nearest branch rather than stopping.

   - **There is no `OrderPlaced` row at all** — and **this does not mean the
     placement was never published.** §9.4's retention purge deletes
     *processed* outbox rows after seven days
     (`RetentionPolicy.OutboxWindow`), while a message sits in the error
     queue until somebody handles it — so on an order older than that window
     the absence is the purge rather than the evidence. `ProcessedAt` would
     not settle it either: it records that the dispatcher published the row,
     not that the saga consumed it.

     **So establish what happened downstream before discarding anything.**
     Read the order's own age and status, look for an
     `ordering.OrderReviews` row, and ask Inventory and Payments whether
     this order id ever reached them. Only where the order is inside the
     retention window, has no review row and neither service has heard of
     it is "nothing downstream ever heard of this order" a conclusion:
     confirm the order reads `Cancelled`, record the message, and discard
     it. Otherwise treat it as case 2 below — a cancellation with real
     downstream work behind it, which is a person's to reconcile.

     > **This bullet said the absence proved it, and that was an
     > instruction to destroy a real cancellation.** Stock reserved and a
     > card authorised eight days ago leave no outbox row and every other
     > trace; discarding on the strength of the missing one loses the only
     > record that the customer asked. **An absence is evidence only where
     > something guarantees the thing would still be there**, and a
     > retention window is precisely the guarantee this repository does not
     > have.
2. **The saga had already finalised, down a branch that escalated.** A
   `not_confirmed` or `not_despatched` timeout finalises the instance and
   leaves the order live, so a customer cancelling afterwards has nothing to
   correlate to; so does a despatch reaching an instance that had already
   observed the cancellation, which finalises on the `MarkOrderShipped` and
   raises `cancelled_after_confirmation` on the way out. **Do not replay** —
   the instance is gone and will not come back. The order is already in front
   of a person: find its row in `ordering.OrderReviews` and take the
   cancellation to whoever is working it
   ([`order-review.md`](order-review.md)), because "the customer has since
   cancelled" changes what that person should do — a `not_despatched` review
   becomes a refund conversation rather than an expedite. Discard with a
   record once it has been passed on.

> **A run of these says placements are not reaching the saga at all.** One is
> an interleaving §9.6 bounds but cannot prevent. Several for the same period
> mean `OrderPlaced` is not reaching `ordering-fulfilment-saga` at all — an
> outbox that stopped, or a binding lost in a rollout — and the cancellations
> are only the visible half of it, because a placement that never arrives is
> silent. Check that endpoint's depth and
> [`outbox-broker.md`](outbox-broker.md) before working them one at a time.

### Fix first — the consumer has a bug

Then neither replay nor discard is right yet. Leave the messages parked, ship
the fix, and replay afterwards. Messages in an error queue are not lost and are
not costing anything; replaying into a consumer that still throws just fills the
queue again and burns the trail.

## Why it reached the error queue at all

Worth answering before closing, because the retry policy is the thing that
failed. §9.8's endpoint configuration sets retry, then the inbox filter, then
an outbox, in that order — **and which outbox depends on the endpoint**.
Ordering's three ordinary endpoints take the in-memory one, which defers sends
until the consumer returns; `ordering-fulfilment-saga` takes MassTransit's
transactional outbox instead
([ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)),
which writes them to `ordering.OutboxMessage` in the saga instance's own
transaction. The **order** is the same on all four and the nesting rule behind
it is unchanged; what differs is what a failure after the commit costs, and on
the saga's endpoint it now costs a redelivery rather than the messages.
If a transient fault exhausted the retries,
the policy may be too tight for that dependency. If a *deterministic* fault
consumed every attempt, retrying it at all was wasted — a poison message should
fail fast, and MassTransit's `Ignore<T>` for known-terminal exception types is
the lever.

**The missing-instance arrivals above are the case where that answer is "it was
supposed to".** Nothing failed: §9.6 chose the error queue as the destination
because it is the only channel that reaches a human.

**What the retries in front of them are worth differs by arrival, and reading
them as waste in both directions is the mistake.** For `PaymentAuthorised` they
are known to be wasted — a deleted saga instance does not return — and are
accepted rather than filtered because excluding them means naming a MassTransit
exception type in `ordering-fulfilment-saga`'s retry policy, and a minute of
backoff on an arrival this rare is cheaper than one endpoint's ladder differing
from every other endpoint's. For an `OrderCancelled` racing its own
`OrderPlaced` they are the mechanism rather than the cost: about seventy
seconds is what the placement still in flight has to land in, and a message
that reaches this queue has already spent it.

## Closing it

The alert clears when the queue is empty. Confirm the messages **went
somewhere** — replayed and processed, or discarded with a record — rather than
having been purged to clear the graph. An empty error queue and a green
dashboard look identical whichever way it happened, which is the same trap
[`outbox-abandoned.md`](outbox-abandoned.md) opens with.
