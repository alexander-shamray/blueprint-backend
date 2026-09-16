# Runbook — stuck saga

| | |
|---|---|
| Alert | `StuckSaga`, in `deploy/observability/alerts/awaiting-signal.yaml` — **not loaded yet** |
| Condition | Unfinalised over an hour outside `Confirmed`, **or over four days in it** |
| Signal | **Owed.** `ordering.saga.oldest_unfinalised.age` does not exist ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

> **This alert cannot fire today, and that is recorded rather than hidden.**
> Nothing publishes a saga-age instrument, so the rule lives in
> `awaiting-signal.yaml` and is not loaded. The queries below work now — they
> read the table directly — so this runbook is usable whether the alert exists
> or not, which is the point of writing it before the signal lands.

## What it means

An order has entered §9.6's fulfilment saga and stopped advancing. Payment may
be taken, stock may be reserved, and nothing is moving it forward.

**Why `Confirmed` has its own threshold rather than the hour.** Every other
state is short: `AwaitingStock` times out in 5 minutes, `AwaitingConfirmation`
in 10, `AwaitingPayment` in 15, and `Compensating` holds for at most 15 past
the moment it is entered — its release wait is 10 minutes, and a payment
verdict it is still owed is bounded by a 15-minute wait of its own. `Confirmed`
is the wait on Shipping, and its timeout is **three days** by design — so an
hour-old saga there is the healthy path, and the alert excludes it from the
hourly branch. A despatch that genuinely expires escalates to
[`order-review.md`](order-review.md), never here.

**The four-day branch is what catches a lost timeout**, and it exists because
excluding `Confirmed` outright left a hole with nothing on the other side of
it. If the `DespatchTimeout` is never delivered — the scheduler failures below —
the saga sits in `Confirmed` for ever, and `OrdersAwaitingReview` cannot fire
either, because that timeout is what creates the review row. So:

- **Fired on the hourly branch** → a short wait is overdue. Read the table
  below for which peer owes an answer.
- **Fired on the four-day branch** → the despatch deadline itself passed
  without escalating. Go straight to the scheduler section; this is a lost
  timeout, not a slow shipper.

## Find them

The state machine persists to `ordering.OrderFulfilmentStates` through
MassTransit's EF repository (§9.6).

```sql
SELECT
    OrderId      = CorrelationId,
    CurrentState,
    StartedAt,
    AgeMinutes   = DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()),
    CancelReason,
    PaymentVerdictOutstanding,
    StockReleaseSettled,
    CancellationObserved
FROM ordering.OrderFulfilmentStates
WHERE (CurrentState <> 'Confirmed'
        AND DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()) > 60)
    OR (CurrentState = 'Confirmed'
        AND DATEDIFF(minute, StartedAt, SYSDATETIMEOFFSET()) > 5760)
ORDER BY StartedAt;
```

**Both branches, because the alert has both.** An earlier version of this query
excluded `Confirmed` outright — so when the four-day branch paged, the first
diagnostic a responder ran returned no rows at all and the saga that caused the
page was the one row it could not show. 5760 minutes is the four days.

**A finalised saga has no row.** MassTransit deletes the instance on
`Finalize`, so anything this returns is by definition unfinalised and the query
needs no completion predicate — which is also why the alert's condition is
phrased as an age rather than as a state count.

**`PaymentVerdictOutstanding` and `StockReleaseSettled` are the diagnosis for
a `Compensating` row and say nothing anywhere else.** That state finalises only
when both of its halves have settled, and `CurrentState` cannot say which one
is still open. A row with `StockReleaseSettled = 1` has already had its
`CancelOrder` sent and is holding for Payments; a row with
`StockReleaseSettled = 0` has sent nothing yet, whatever the verdict flag says.
In every other state `PaymentVerdictOutstanding` merely records that
`AuthorisePayment` went out and no answer has come back — true and not a join.

**`CancellationObserved` is the opposite: it means nothing in `Compensating`
and is the whole diagnosis in the states that can carry it.**
[§3.2](../backend-architecture/03-bounded-contexts.md) has Inventory consuming
`OrderCancelled` directly
([ADR-029](../backend-architecture/adr/ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)),
so a `StockReleased` arriving in a state that sent no `ReleaseStock` proves a
cancellation reached Inventory before this saga consumed its own copy — and
since [#143](https://github.com/alexander-shamray/blueprint-backend/issues/143)
the machine records that on the instance instead of absorbing it. A `1`
therefore reads as **a cancellation is in flight and this instance knows**. It
is never cleared, so it says nothing about how long ago that was.

Group by `CurrentState` first — one stuck order is a message; twenty in the same
state is a dependency.

## Read the state, then ask what it is waiting for

Every state but one waits on a single answer with a single timeout behind it,
and that timeout should already have fired. `Compensating` is the exception,
and the table says so.

| State | Waiting for | Timeout | If the timeout fires |
|---|---|---|---|
| `AwaitingStock` | `StockReserved` from Inventory | 5 min | Cancels the order, `stock_timeout` |
| `AwaitingPayment` | `PaymentAuthorised` / `PaymentDeclined` | 15 min | Cancels and releases stock, `payment_timeout` |
| `AwaitingConfirmation` | `OrderConfirmed` from **Ordering itself** | 10 min | Escalates to `OrderReviews`, `not_confirmed` |
| `Confirmed` | `ShipmentDispatched` from Shipping | 3 days | Escalates to `OrderReviews`, `not_despatched` |
| `Compensating` | `StockReleased` from Inventory, **and** a payment verdict from Payments wherever one is still owed | 10 min on the stock half, 15 min on the verdict | Stock: cancels the order and escalates, `stock_not_released`. Verdict: stops waiting, no row |

**`Compensating` finalises when both halves have settled, not when either
does.** It is reached from `AwaitingPayment` with `AuthorisePayment` already
sent, so Inventory and Payments can both owe an answer and
[§9.4](../backend-architecture/09-messaging.md) orders nothing between them.
Each stock exit — `StockReleased`, or the release timeout giving up on it —
sends `CancelOrder` and finalises *only if* no verdict is outstanding; each
payment arrival — `PaymentAuthorised`, `PaymentDeclined`, or the payment
timeout — clears the verdict and finalises *only if* the stock half has
settled. So a `Compensating` row past ten minutes is not by itself a lost
release.

**Only some doors into the state owe a verdict, which is why the table hedges
rather than asserting.** A cancellation arriving in `AwaitingPayment` and the
fifteen-minute payment timeout both enter `Compensating` with Payments still
unanswered — the timeout deliberately leaves the obligation standing and arms
the wait once more, because a PSP that has not answered has not declined. A
decline does not, and neither does anything arriving through
`AwaitingConfirmation`, because a `PaymentAuthorised` is what got it there.

**A `1` in `CancellationObserved` changes what the row is waiting for, and in
two states it is a new way for a saga to sit still.** The instance has seen an
early `StockReleased`, so it knows a cancellation is in flight, and every
forward transition in those states asks before acting (#143):

- **`AwaitingStock`** — a `StockReserved` arriving now is **withheld**: no
  `AuthorisePayment`, no transition, and the instance stays here. What it is
  waiting for is the saga's own copy of the `OrderCancelled`, whose branch in
  this state compensates properly. **`StockTimeout` is deliberately left
  armed** and is the backstop: the five-minute wait armed when the order was
  placed still fires, cancels the order with `stock_timeout` — a no-op the
  aggregate absorbs, the customer's cancellation having committed already —
  and finalises, so nothing sits here indefinitely.
- **`AwaitingPayment`** — a `PaymentAuthorised` arriving now clears the
  verdict, raises `payment_authorised_during_compensation` and **withholds
  `ConfirmOrder`**; the instance stays here, waiting for the same cancellation.
  **`PaymentTimeout` is deliberately left armed** — the fifteen-minute wait
  armed when `AuthorisePayment` was sent still fires and compensates exactly as
  it would have. A row here with `CancellationObserved = 1` and
  `PaymentVerdictOutstanding = 0` has already escalated the money: expect a
  review row ([`order-review.md`](order-review.md)), and do not read the
  cleared verdict as nothing having happened.
- **`AwaitingConfirmation` and `Confirmed`** — nothing is withheld, and the
  flag adds a `cancelled_after_confirmation` row on two different kinds of
  exit. A `ShipmentDispatched` still sends `MarkOrderShipped` and
  **finalises**, so the row is written on the way out and the instance is
  gone. An `OrderConfirmed` in `AwaitingConfirmation` raises the same row and
  then **transitions to `Confirmed`**, so the instance is **alive** and can
  sit out the three-day despatch wait — which is the one path where this flag
  and a long-lived row go together, and the reason `Confirmed` has its own
  four-day branch in this alert rather than the hour.

  So a row in `Confirmed` with `CancellationObserved = 1` is **not** an
  instance sitting still because of the flag: it is waiting on Shipping, as
  any confirmed order does, with a cancellation already escalated. Read the
  `OrderReviews` row before treating it as a stall.

**Past the hour this alert measures, an instance withholding a forward step is
a missing timeout rather than a missing cancellation.** Both backstops are
minutes long, so an hour-old instance in `AwaitingStock` or `AwaitingPayment`
has outlived the wait that was supposed to end it whatever the flag says — the
scheduler section below is the procedure, and the cancellation is the second
thing to chase rather than the first.

**A saga older than every timeout its state is waiting on is a timeout that
did not arrive**, and that is a different fault from the peer being slow. It
is still the first thing to check, because it has one cause far more often
than not. For `Compensating` "every timeout" means both of them, and neither
is measured from the `StartedAt` the query reports — that column is when the
order was placed. Read `StockReleaseSettled` and `PaymentVerdictOutstanding`
instead: they answer the same question without the arithmetic. Past the hour
this alert measures, both waits have long expired either way, so a live
`Compensating` instance there is a missing timeout whichever half is open.

**`AwaitingConfirmation` is the exception to everything below, because there is
no peer.** The saga sent `ConfirmOrder` to Ordering's own `ordering-commands`
queue and is waiting for the `OrderConfirmed` the aggregate publishes when it
commits — so a saga stuck there is **this service** failing to consume its own
command, and the scheduler section below is the wrong place to start. Go
straight to the outbox query further down and look for the `ConfirmOrder` that
never left, then at whether `ordering-commands` is being drained at all. The
aggregate *refusing* the command does not land here: that is a `Rule` failure
`CommandConsumer` acks, and the cancellation behind it moves the saga to
`Compensating` on its own event.

## The timeout scheduler is the usual culprit

[ADR-021](../backend-architecture/adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)
schedules every saga timeout on RabbitMQ's **delayed message exchange**, which
is why §14.1's RabbitMQ is the one infrastructure image that is *built* rather
than pulled. Three ways that breaks, all of which look like a hung saga:

1. **The plugin is missing.** A broker replaced with a stock image does **not**
   silently drop the scheduled message — measured, and recorded in
   `deploy/compose/rabbitmq/Dockerfile`. The bus starts clean; the first
   `.Schedule(…)` fails `exchange.declare` with `precondition_failed: unknown
   exchange type 'x-delayed-message'`, and **MassTransit then retries the
   topology for ever**. The call never returns, the transition never completes,
   and the only trace is a channel error in the **broker's** log every few
   seconds while the service stays healthy and quiet.

   That distinction is what you look for: not a missing message, but a
   repeating channel error on RabbitMQ and a saga frozen mid-transition.

   ```bash
   kubectl -n <ns> exec deploy/rabbitmq -- rabbitmq-plugins list | grep delayed
   kubectl -n <ns> logs deploy/rabbitmq --since=10m | grep -i 'precondition_failed\|x-delayed-message'
   ```

2. **The scheduler is not registered.** A registration nothing resolves at
   startup fails at the first message, not at boot — `ValidateOnBuild` never
   constructs an open generic and no host resolves a scheduler while it boots,
   so the service connects, declares, and reports ready with no scheduler at
   all. This is recorded in [`docs/lessons.md`](../lessons.md) as a lesson
   because it cost a debugging session: the symptom is timeouts that never
   arrive and a log that says nothing.

3. **The broker lost its delayed messages.** A delayed exchange holds them in a
   node-local store; a node that was rebuilt loses whatever was pending. Nothing
   reports this — the messages simply never arrive.

If the plugin and registration are fine, check the broker lane is moving at
all: a stalled outbox means messages are not leaving the service, which is
[`outbox-broker.md`](outbox-broker.md) rather than this.

> **The saga's own `Send`s are not in the lane that runbook drains, and this
> page said they were.** §9.4's `ordering.OutboxMessages` holds what a
> **handler** stages; a saga sends on the bus, so its `CancelOrder`,
> `ConfirmOrder`, `FlagOrderForReview` and scheduled expiries never appear
> there. Since
> [ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
> they are written to `ordering.OutboxMessage` — MassTransit's table, singular
> — in the transaction that commits the instance, and delivered after it. Two
> tables, two mechanisms, and the one to look in is decided by *who sent the
> message* rather than by which order it concerns.

## Or the peer never answered

If timeouts are working and the saga is younger than every timeout its state
is waiting on, it is simply waiting and there is nothing wrong yet — which for
a `Compensating` instance holding an outstanding verdict means the payment
wait as well as the release one. If it is *older* and the timeout fired
but the state did not change, look for the command that was sent:

```sql
SELECT
    MessageId,
    MessageType,
    Lane,
    Attempts,
    ProcessedAt,
    LastError = LEFT(LastError, 500)
FROM ordering.OutboxMessages
WHERE CorrelationId = @OrderId
ORDER BY OccurredAt;
```

**That table is the wrong one for a command the saga sent, and the query is
kept because the row it *does* find is still worth having.** What
`ordering.OutboxMessages` holds is what a handler staged — an `OrderCancelled`
that the `CancelOrder` produced, for instance — so a row here proves the
command was consumed and the aggregate moved. Its **absence** proves nothing
about the command, only that nothing downstream of it has committed yet.

**For the command itself, look in `ordering.OutboxMessage`** — MassTransit's
table, singular, where
[ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
puts every `Send` and `Schedule` a transition makes, in the instance's own
transaction. Rows there are delivered after that transaction commits, and each
is joined to its triggering delivery through `InboxMessageId`/`InboxConsumerId`
into `ordering.InboxState`, whose `LastSequenceNumber` is how far that
delivery's sends have got. **These are MassTransit's tables and no SQL this
repository owns touches them** — which is not the same as their being
inert, and the difference matters at 3am. The running service writes them
through the library's own outbox filter, in the transaction that commits the
instance, and then clears them by two different mechanisms neither of which is
ours:

- A row in `ordering.OutboxMessage` is **removed by the outbox middleware once
  the message reaches the transport.** So a row here going away means the
  command was *sent*, and finding none is the normal steady state rather than
  evidence of a purge.
- A row in `ordering.InboxState` is removed by the hosted
  `InboxCleanupService<OrderingDbContext>` that `AddEntityFrameworkOutbox`
  registers alongside the filter, on a timer, once the duplicate-detection
  window has elapsed. That service reads **this table only.**

`ordering.OutboxState` is the third table and is not part of either story:
it belongs to the bus-side outbox, `UseBusOutbox()` is deliberately not called,
and so **nothing writes, reads or prunes it in this deployment.** It exists
because `OutboxMessage.OutboxId` carries a foreign key to it. An empty
`ordering.OutboxState` is the design; do not spend an incident on it.

§9.4's `RetentionPurgeService` — which prunes *our* tables — reads none of
the three. What this repository has no query for, it also has no opinion
about: read the library's schema before drawing a conclusion from a column.
What is settled here is *which* table to open. **Before ADR-032 there
was no table to look in at all**, which is what made a lost command
indistinguishable from a command nobody sent — the defect
[#128](https://github.com/alexander-shamray/blueprint-backend/issues/128)
closed.

**For a saga in `AwaitingConfirmation` this is the first query rather than the
last**, and what it is looking for is a `ConfirmOrder`. There is no peer to have
gone quiet: the command is Ordering's own, so a row still sitting here unsent —
or sent and never consumed, which the `ordering-commands` queue depth shows —
is the whole fault. **Read the order's own status rather than assuming it.**
Normally it is still `AwaitingPayment` with the card authorised, which is why
that wait escalates rather than compensating when its ten minutes run out. If
it is already `Confirmed`, the command committed and it is the acknowledgement
that went missing — a different incident, and
[`order-review.md`](order-review.md)'s `not_confirmed` procedure branches on
exactly that.

## Manual compensation

**Last resort, and only with the order read first.** The saga sends commands to
`ordering-commands` (§9.4); the honest way to unstick one is to publish the
event it is waiting for, or the timeout it missed, rather than editing the state
row. Editing `CurrentState` directly leaves the scheduled messages armed and the
aggregate unaware, and produces a saga that is inconsistent with its own order.

**Most of these transitions have no HTTP route, and reaching for one is how an
incident stalls.** `Ordering.Api` exposes exactly two: `POST /api/v1/orders`
and `POST /api/v1/orders/{id}/cancel`. `ConfirmOrder`, `MarkOrderShipped` and
`FlagOrderForReview` are **broker-only** — the saga sends them to
`ordering-commands`, and that endpoint is their only ingress.

| To do this | Use |
|---|---|
| Cancel the order | The API's cancel endpoint — the domain rules run, and §11.4's ownership check applies |
| Confirm, ship, or flag for review | Publish the command to `ordering-commands`; there is no route |
| Advance the saga past a wait | Publish the *event* it is waiting for, not a command |

Whichever ingress, it goes through the domain rather than around it — §5's
`Order` refuses transitions that do not make sense, and that refusal is a
feature here.

**`AwaitingConfirmation` is where that last row bites hardest, so do not take
it literally there.** The event it waits for is `OrderConfirmed`, and
publishing one by hand would be stating a fact the aggregate has not stated —
the order would still be `AwaitingPayment`, the saga would move to `Confirmed`,
and Shipping would be told to despatch an unconfirmed order. Redeliver the
`ConfirmOrder` instead and let the aggregate publish its own acknowledgement.
The distinction the table draws is between commands and events; the rule under
it is that you may replay a message, never invent one.

**`Compensating` is where one event may not be enough**, because it is waiting
on two halves rather than one. Publishing the `StockReleased` it is missing
sends the `CancelOrder` and settles the stock half — and if Payments still
owes a verdict the instance stays, until that verdict arrives or the payment
wait expires. That is the machine working rather than a second fault. Read
`PaymentVerdictOutstanding` before publishing anything else, and never invent
a `PaymentAuthorised` to clear it: that states money moved, and Payments'
answer is the only thing entitled to say so.

**An instance with `CancellationObserved = 1` is waiting for a message that
was already published**, which narrows where to look before anything is
published by hand. Inventory released, so the `OrderCancelled` left Ordering's
outbox and reached the broker — what has not happened is this endpoint
consuming its own copy. Check `ordering-fulfilment-saga`'s depth and its
`_skipped` and `_error` queues ([`skipped-queue.md`](skipped-queue.md),
[`error-queue.md`](error-queue.md)): the message is usually parked rather than
lost, and getting it delivered is the whole recovery. Composing a fresh one is
the invention this section forbids — and one carrying an `Origin` of `user`
against an instance that has since finalised is exactly the arrival §9.6 now
faults, so it costs a page as well as a lie.

**`PaymentAuthorised` and `OrderCancelled` now FAULT when they find no
instance, and replaying either against a finalised saga pages someone.**
`PaymentAuthorised` is answered `OnMissingInstance(m => m.Fault())`, because
Payments produces it and it can therefore never be Ordering's own echo
arriving after the workflow ended — an authorisation with no instance means
money moved on an order this saga cancelled. `OrderCancelled` is answered by a
branch rather than by a default
([#123](https://github.com/alexander-shamray/blueprint-backend/issues/123)):
the saga's own echo — an `Origin` of `workflow` — is discarded, and so is an
absent `Origin`, which is a rolling deploy publishing from before the field
existed. **Every other origin faults**, and `user` is the one that matters: a
cancellation somebody asked for, arriving at a saga that is not there. Both
land in the error queue [§13.6](../backend-architecture/13-observability.md)
pages on, with the message retained, and
[`error-queue.md`](error-queue.md) works them.

**The rest of the machine's events are still consumed cleanly when nothing
correlates**, on MassTransit's default — `StockReleased` included, which
[ADR-024](../backend-architecture/adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)
makes answerable for every release. An earlier version of this
paragraph named `OrderCancelled` among them, and cited its registration as
where that default was written down. That registration now decides instead of
restating, so the sentence had to change with it.

Record every manual action against the `OrderId`. The next person to read this
order will find a state machine that moved without a message, and the only
explanation will be whatever you wrote down.
