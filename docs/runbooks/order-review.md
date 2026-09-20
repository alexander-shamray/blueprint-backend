# Runbook — orders awaiting review

| | |
|---|---|
| Alert | `OrdersAwaitingReview`, in `deploy/observability/alerts/awaiting-signal.yaml` — **not loaded yet** |
| Condition | Any row in `ordering.OrderReviews` older than 1 hour |
| Signal | **Owed.** `ordering.order_reviews.oldest.age` does not exist ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The service team ([§13.8](../backend-architecture/13-observability.md)) |

> **This alert cannot fire today.** Nothing publishes an instrument over
> `OrderReviews`, so the rule sits in `awaiting-signal.yaml`, unloaded. The
> table exists and the queries below work now — the queue is real, only the
> pager is missing.

## What it means

A saga reached something it could not compensate and escalated (§9.6). Three of
the five reasons are a wait that ran out — `not_despatched`,
`stock_not_released`, `not_confirmed` — and the other two are an authorisation
**Ordering** has no command to undo.

**Those last two are not told apart by the saga state, and this page used to
say they were.** The split is `cancelled_after_confirmation` where Shipping was
told and a despatch may be moving, `payment_authorised_during_compensation`
where it was not. That used to map one-to-one onto `Confirmed` and
`Compensating`; since [#126](https://github.com/alexander-shamray/blueprint-backend/issues/126)
it does not, because `cancelled_after_confirmation` is raised from
`Compensating` as well — whenever an `OrderConfirmed` lands there, which is
precisely the evidence that Shipping was told. **And
[#143](https://github.com/alexander-shamray/blueprint-backend/issues/143)
widened both codes again**: a despatch arriving at an instance that has already
observed a cancellation raises `cancelled_after_confirmation` from
`AwaitingConfirmation` or `Confirmed`, and an authorisation arriving at one
raises `payment_authorised_during_compensation` from `AwaitingPayment`, where
no compensation has started. **The code still means one thing. What no longer
follows from it is where the saga is, or whether there still is one.**

**"The platform has no contract to undo it" is what this said, and it is
wider than the truth.** Payments consumes `OrderCancelled` and voids an
authorisation already taken (§3.2); what Ordering lacks is a refund *command*
to send. The difference decides step 1 below, which is why the page cannot
open by collapsing it.

**Check whether the saga is still running before you work the row**, because
that is not the same answer for every reason and an earlier version of this
page assumed it was:

| Reason | State of the saga |
|---|---|
| `not_despatched` | Finalised. The state row is gone and this row is the only trace |
| `stock_not_released` | **Finalised on the release timeout only if Payments owed nothing.** That exit sends `CancelOrder`, raises this row, and then finalises conditionally, so an order cancelled while an authorisation was in flight keeps its instance until the verdict lands or the payment wait expires ([#124](https://github.com/alexander-shamray/blueprint-backend/issues/124)). Query the state table |
| `not_confirmed` | Finalised, on the ten-minute confirmation timeout |
| `cancelled_after_confirmation` | **Depends on which state raised it, and the row does not say.** From `Confirmed` it is finalised — the branch cancels nothing and finalises. From `Compensating` it is raised **mid-wait** and the instance stays until the stock half settles — `StockReleased`, or the ten-minute `ReleaseTimeout`. No payment verdict can be outstanding on that path: the only door onto it runs through `AwaitingConfirmation`, which the saga enters on a `PaymentAuthorised`. And [#143](https://github.com/alexander-shamray/blueprint-backend/issues/143) added two more raisings that differ from each other as much as from the pair above. The **despatch** branches in `AwaitingConfirmation` and `Confirmed` raise it when a cancellation had already been observed: both send `MarkOrderShipped` and finalise, so the instance is gone **and a parcel has left**. The **confirmation** branch in `AwaitingConfirmation` raises it when an `OrderConfirmed` arrives after a cancellation was observed, and then transitions to `Confirmed` — so the instance is **alive**, no parcel has necessarily left, and it can sit there for the three-day despatch wait. That is the one raising whose row can outlive this alert's hour by days. Query the state table; the procedures differ at step 2 |
| `payment_authorised_during_compensation` | Raised mid-wait, when an authorisation lands after compensation has BEGUN — which is not the same as after a cancellation: `Compensating` is also reached from `PaymentDeclined` and the fifteen-minute payment timeout, where no `OrderCancelled` exists yet. The branch that raises this row is also the one that answers the payment half, so the stock half is all that is left: the instance stays until `StockReleased` or the ten-minute `ReleaseTimeout`, both well inside the hour this alerts on, and by the time you read the row it has normally finalised. Check, and branch. A `stock_not_released` row may join it. **Since [#143](https://github.com/alexander-shamray/blueprint-backend/issues/143) `AwaitingPayment` raises it as well, without entering `Compensating` at all**: an early `StockReleased` recorded the cancellation, the authorisation was escalated onto this row instead of confirming the order, and the instance **stays in `AwaitingPayment`** waiting for its own copy of the `OrderCancelled`, with `PaymentTimeout` still armed behind it |

For the finalised cases [`stuck-saga.md`](stuck-saga.md) will not catch this,
which is why §13.6 gives it a row of its own rather than folding it into the
saga-age alert. **For a row raised mid-wait the saga-age alert usually will not
fire either, and an earlier version of this page said it would.** Both
thresholds are an hour, and every wait `Compensating` can be holding for is far
shorter — ten minutes on the stock half, fifteen on a payment verdict it is
still owed — so by the time such a row alerts the instance is normally long
gone. **The same arithmetic covers the `AwaitingPayment` raising #143 added**,
which holds an instance outside `Compensating` altogether: its backstop is the
fifteen-minute `PaymentTimeout`, so it too is minutes against this alert's
hour. The two alerts coincide only when the wait that is holding the instance
outlived its own timeout, and then they are the same incident.

**"The only one that can still be running" is what this table said of
`payment_authorised_during_compensation`, and no count belongs in that sentence
at all.** The rule is that a row raised by a `Compensating` branch which does
not finalise leaves an instance behind — and since
[#124](https://github.com/alexander-shamray/blueprint-backend/issues/124)
even the branches that *do* finalise finalise conditionally, because that state
joins on Inventory and on Payments and settles one half at a time. So "has the
saga finished" is a question about the **branch** that raised the row and about
what was still outstanding when it ran, not about the reason code — and the
reason code is all the row persists.

A row means "a human still needs to look at this". The table is a **work queue,
not a log**: there is no `ResolvedAt` column, and resolving a review means
deleting the row.

## The queue

```sql
SELECT
    OrderId,
    Reason,
    RaisedAt,
    AgeHours = DATEDIFF(hour, RaisedAt, SYSDATETIMEOFFSET())
FROM ordering.OrderReviews
ORDER BY RaisedAt;
```

The key is `(OrderId, Reason)`, so **one order can legitimately carry two
rows** — a release timeout raises `stock_not_released` beside whatever cancelled
the order. Two rows for one order is not a duplicate; two rows with the same
pair is impossible.

**The pairing worth recognising is `cancelled_after_confirmation` followed by
`stock_not_released`.** The first, raised from `Compensating`, means a release
went out for stock that may be being picked; the second means that release
then timed out. Read together they are one incident with two loose ends — a
despatch to stop and a reservation in an unknown state — and neither row says
the other exists.

## What each reason means

All from `ReviewReasons` in `Common.Contracts`. An unknown code is
a bug, not a new category — `FlagOrderForReviewMapper` refuses one on the
first attempt (§9.8), because `Reason` is half this table's primary key and a
typo opens a second row nobody resolves rather than overwriting the first.

### `not_despatched`

The despatch timeout fired: three days after the order was confirmed, Shipping
had not reported `ShipmentDispatched`.

**Payment has been taken and stock is gone.** This is the wait §9.6 gives no
automatic compensation precisely because there is no safe automatic answer — the
customer has paid for something that has not shipped.

Work it in this order:

1. **Did it actually ship?** Check Shipping for a despatch that happened but
   whose event never arrived. If so this is a messaging failure and the outbox
   runbooks apply on the *Shipping* side; the customer is fine.
2. **Is it still shippable?** If stock is physically there, expedite and let the
   real `ShipmentDispatched` land.
3. **If not**, this becomes a refund and a customer conversation. Neither is
   automatable from here, which is the whole reason a human was paged.

### `stock_not_released`

Compensation itself timed out: the saga cancelled an order, asked Inventory to
release the reservation, and ten minutes later `StockReleased` had not arrived.

**Stock nobody can sell.** The order is already failing — this is the worst
place for a saga to be stuck, which is why §9.6 gives compensation a timeout
like any other wait.

**This row means the release did not complete, and since
[ADR-024](../backend-architecture/adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)
it means nothing else.** A `ReleaseStock` publishes `StockReleased` whether or
not a reservation was held, so "there was never anything to release" is no
longer one of the ways to get here — which used to be reachable through
`StockReservationFailed`, an event that *proves* no reservation exists. Before
that was settled, step 1 could legitimately find nothing and the row was
telling you about a contract gap rather than about stock. It is now always
about stock or about delivery.

**This row no longer implies a finalised saga**, and it is the one reason code
whose live instance means the *money* half rather than the stock one. The
release timeout sends `CancelOrder`, raises this row, and then finalises only
if Payments owes nothing — so an order cancelled while an authorisation was
still in flight keeps its instance until that verdict arrives or the payment
wait expires (#124). The cancellation has already gone out either way, which
is the opposite of `payment_authorised_during_compensation`, where a live
instance means no `CancelOrder` has been sent at all. Read the instance in
[`stuck-saga.md`](stuck-saga.md); the steps below are about the stock, and
they hold whether or not one is still there.

1. Check Inventory for the reservation. If it was released and the event was
   lost, reconcile and move on.
2. If it is still held, release it — through Inventory's own API so its
   invariants run, never by editing its tables.
3. Confirm the order really did cancel: the review row says compensation
   stalled, not that the cancellation failed.

### `not_confirmed`

The confirmation wait timed out: payment was authorised, the saga sent
`ConfirmOrder`, and ten minutes later the `OrderConfirmed` the aggregate
publishes on commit had not arrived.

**This is the only reason whose far end is this service.** Every other wait in
§9.6 bounds a peer — Inventory, Payments, Shipping — where this one bounds
Ordering consuming its own command off `ordering-commands`. So the diagnosis
starts at home, and a *spike* in these rows is an Ordering fault rather than a
dependency signal.

**What it does not mean is that the aggregate refused.** A `ConfirmOrder` the
order rejects — because it was cancelled underneath, or had already moved
on — returns `order.not_awaiting_payment`, which is an `Error.Rule`;
`CommandConsumer` acks it, counts a domain rejection and logs it, and nothing
reaches an error queue. The cancellation that caused it reaches the saga on its
own `OrderCancelled` and moves it to `Compensating`. So a `not_confirmed` row
means the command was **never consumed at all**.

**Payment is taken and stock is held.** Like `not_despatched`, this wait has no
automatic compensation: §3.2 closes Ordering's Accepts column at
`AuthorisePayment`, so there is no refund command, and cancelling the order
without one would leave the customer charged.

1. **Find the `ConfirmOrder`.** Query `ordering.OutboxMessages` for the order
   (the query is in [`stuck-saga.md`](stuck-saga.md)). A row still unsent is an
   outbox fault and the outbox runbooks apply; a row dispatched but never
   consumed points at `ordering-commands` depth or at a consumer that is down.
2. **Check the order's own status.** It should still be `AwaitingPayment`. If it
   is `Confirmed`, the command *did* commit and it is the `OrderConfirmed` that
   was lost — the saga has already finalised on the timeout, so reconcile
   forward: Shipping needs the confirmation, and the three-day despatch wait was
   never armed.
3. **Redeliver rather than invent.** Replay the `ConfirmOrder` and let the
   aggregate publish its own acknowledgement. Publishing an `OrderConfirmed` by
   hand states a fact the aggregate has not stated, and Shipping acts on it.
4. **If the order cannot be confirmed**, this is a refund and a customer
   conversation, exactly as `not_despatched` step 3.

### `cancelled_after_confirmation` and `payment_authorised_during_compensation`

An order is being cancelled and its payment is authorised. Undoing an
authorisation is a **refund**, and
[§3.2](../backend-architecture/03-bounded-contexts.md) closes Payments' Accepts
column at `AuthorisePayment` — Ordering has no refund *command* to send.

**That is not the same as no automatic refund, and this page read it that
way.** §3.2 also gives Payments a `Refund` aggregate, has it publish
`PaymentRefunded`, and lists `OrderCancelled` in its **Consumes** column — and
`OrderCancelled`'s own contract says in as many words that "an authorisation
that was taken is voided". Payments refunds off the event, autonomously.
Ordering simply has no way to *ask*, which is a different sentence from
nobody doing it. **So the first act is never to refund.**

**Whether the void has already happened is not knowable from the code, and
that is the whole reason step 1 is a check.** An earlier revision of this
paragraph said `cancelled_after_confirmation` has its refund on the way while
`payment_authorised_during_compensation` is beyond the automatic path's
reach. Neither half survives
[§9.4](../backend-architecture/09-messaging.md):

- The saga seeing `OrderCancelled` before an authorisation says nothing about
  when **Payments** consumed it. They are two independent consumers of two
  independent messages, and §9.4 orders nothing between them — the same fact
  that makes a release land before its own reserve (#125).
- And on the decline and payment-timeout doors **no `OrderCancelled` has been
  published yet** when the row is raised: `CancelOrder` goes on
  `Compensating`'s exit, so the cancellation — and the void that follows
  it — is still to come.

So the event is published on every path that raises either code, and on no
path can you infer from the code whether Payments has acted. **Check, on
both.**

**"Published on every path" assumes the saga gets out of `Compensating`,
and step 2 is where you find out whether it did.** Both stock settlements —
`StockReleased`, and the ten-minute `ReleaseTimeout` giving up on it — send
`CancelOrder`, and on *these two codes* nothing else is holding the instance:
`payment_authorised_during_compensation` is raised by the branch that answers
the payment half, and `cancelled_after_confirmation` from `Compensating` is
reached only through `AwaitingConfirmation`, which the saga enters on a
`PaymentAuthorised`. So an instance still live at the hour this alerts on has
had neither settlement, which means the cancellation has not been sent and
will not be until someone intervenes.

**Neither of #143's raisings runs through `Compensating`, and one of them
inverts that conclusion.** A `cancelled_after_confirmation` raised on a
despatch has already finalised, so there is no instance to find. A
`payment_authorised_during_compensation` raised in `AwaitingPayment` is live —
and there the cancellation **has** been published: the aggregate cancelled, and
Inventory releasing off that event is what the instance observed. What has not
happened is this saga consuming its own copy, and no `CancelOrder` is owed at
all, because the order is already cancelled. So read `CurrentState` before
applying the sentence above — in `Compensating` a live instance means no
cancellation went out, and in `AwaitingPayment` it means one did.

**That inference is narrower than it reads and does not travel to every
row.** `Compensating` finalises only when the stock half and the payment
verdict have both settled (#124), so in general a live instance says nothing
about whether `CancelOrder` went out — on a `stock_not_released` row it has,
and the instance is being held for the money. These two codes are the case
where the payment half is discharged by construction, which is why the
conclusion survives here and has to be argued rather than assumed.

**Deal with the saga before the money, and this page said the opposite.** It
read "refund by hand and treat the saga as the separate incident it is",
which is a double refund by a slower route: a live instance that later
resumes — because `StockReleased` finally arrives, or because someone
restarts the scheduler — sends `CancelOrder`, the aggregate publishes
`OrderCancelled`, and Payments voids off it. A manual refund on a live
instance is therefore a refund **ahead of** an automatic one, not instead of
it. Nothing in the platform suppresses that second refund: §3.2 gives
Ordering no refund command, so it has no way to say "already done" either.

So drive the instance to an exit first — [`stuck-saga.md`](stuck-saga.md), a
live instance past its own timeout being a scheduler incident — and let the
cancellation it sends do the refunding. **If the authorisation genuinely
cannot wait**, refunding by hand is still available and you own the
reconciliation: record it, and check for `PaymentRefunded` again once the
saga finally exits, because the second one is coming and this page cannot
stop it.

**What actually separates the two is Shipping.**
`cancelled_after_confirmation` means the aggregate confirmed and Shipping was
told, so a despatch may be in motion and stopping it comes first;
`payment_authorised_during_compensation` means it was not told and there is
nothing to stop. The money is what both rows have in common rather than what
tells them apart.

**That is a distinction between the codes and no longer between the states,
and it is the change this page most needs its reader to know
([#126](https://github.com/alexander-shamray/blueprint-backend/issues/126)).**
Until then `cancelled_after_confirmation` came only from `Confirmed`, so the
code and the state were the same fact and this page navigated on either. Now
`Compensating` raises it too — on an `OrderConfirmed` arriving after that
state was entered, which is exactly the evidence Shipping was told. **The code
is still the discriminator for Shipping. The state is not, and is now a
separate question you have to ask.**

**Two things follow, and the second is the one that surprises.** The saga may
still be live when the row is `cancelled_after_confirmation`, so the
"deal with the saga before the money" rule above applies to it as well. And on
that path a `ReleaseStock` **has already gone out** — the state was entered on
the premise that no confirmation had happened — for stock a picker may be
holding. Nothing can recall it: §3.2 gives Inventory no way to be told to keep
a reservation after all. Reconcile the reservation with Inventory as part of
stopping the despatch, rather than assuming the release was correct.

**That is a difference in what the saga sent, not in where the stock ended
up**, and reading it as the latter is the mistake this page used to invite.
§3.2 has Inventory consuming `OrderCancelled` **directly**, so on *either* path
the cancellation itself told it to release. **Expect the reservation to be free
on both**, and treat the `Confirmed` path's missing `ReleaseStock` as one fewer
message rather than as stock still held.

**Only one of the two is necessarily a customer cancelling**, and this paragraph
said both were. `cancelled_after_confirmation` always follows an
`OrderCancelled` — arriving at the saga in `Confirmed` directly or in
`AwaitingConfirmation` on the way to `Compensating`, or, on the despatch
branches #143 added, arriving at *Inventory* and coming back as the
`StockReleased` the instance recorded. Every route starts with somebody
cancelling the order. `payment_authorised_during_compensation` does not:
it is raised when an authorisation arrives while a cancellation is under
way — which the saga reaches by compensating on a cancellation, a decline
**or** a fifteen-minute payment timeout, and, since #143, by observing a
cancellation in `AwaitingPayment` without compensating at all. A slow PSP that
authorises after the timeout produces that row with nobody having cancelled
anything; the #143 raising is at the other end, and cannot happen without a
cancellation.

**Step 1 is the same either way and its answer is not predictable from the
code. Steps 2 and 3 differ, and the CODE is what tells you which** —
`cancelled_after_confirmation` means there is a despatch to stop,
`payment_authorised_during_compensation` means there is not. Read the code, not
the saga state: the state answers a different question, which is whether the
instance is still live and therefore whether step 1's refund can wait.

**These used to be one code, and this page selected on a saga state instead.**
That does not work: `ordering.OrderReviews` persists `(OrderId, Reason,
RaisedAt)` and nothing else, and by the hour the alert measures the saga has
usually finalised — so the state the table above asks you to check is gone. An
earlier version had only the `Confirmed` procedure, which sent an on-call
looking for a despatch that does not exist; keying it on a vanished state was
the same defect one step less obvious.

**And the state stopped being a proxy for the code entirely**, which is why the
instruction above now says so twice. `cancelled_after_confirmation` is raised
from two states with different saga lifetimes and the same procedure; the
persisted code carries the procedure, and nothing persists the branch. That is
the design working rather than a gap: what an operator needs from the row is
what to *do*, and the row says it.

1. **Find out whether Payments already refunded it. This decides which
   branch you are in, and never on its own that you should pay.** Read
   `GET /api/v1/payments/{orderId}` through the gateway with a
   `payments:admin` token: a `refund` in the answer means Payments voided it,
   and `intent.status` says whether there was anything to void. The provider's
   own console is the second source, for when Payments is itself the question.
   This is the same act on both codes and neither answer is the expected
   one — see above — so the check is the work, not a formality before the
   refund.

   **"Then refund only if it did not" is what this step said**, and it
   contradicted the two paragraphs directly below it: finding no refund is
   the start of the saga check, not a licence to pay. Every refund
   instruction on this page now sits after the saga's state is known, for
   the reason those paragraphs give.

   **A cancellation may still be in flight**, which is the case worth
   naming: on `payment_authorised_during_compensation` reached from a
   decline or a timeout, the `CancelOrder` that triggers the void has not
   been sent yet when this row appears. If you find no refund and no
   cancellation, **check the saga before doing anything else** — step 2 of
   the `Compensating` procedure below.

   **A gone instance is not an answer on its own; a live one means fix the
   saga, not the money.** On both of these codes the payment half is
   already answered, so for an instance in `Compensating` the two stock
   settlements are the only thing that can end it — and one still live at
   the hour this alerts on has missed the ten-minute release timeout, so no
   `CancelOrder` is coming without intervention. **An instance in
   `AwaitingPayment` is the #143 raising and ends differently**: it is
   waiting for a cancellation the aggregate has already published, and the
   `PaymentTimeout` behind it should have fired long before this hour. Either
   way waiting waits for ever — and refunding races an automatic void that
   arrives the moment the saga is unstuck, so the fix is the saga.

   **And a gone one still has to be checked**, though no longer for the
   reason this line used to give. #128's crash window could delete the
   instance with its `CancelOrder` never sent;
   [ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
   closed it, so a finalised instance now proves the command was *staged*
   in the transaction that finalised it. What it still does not prove is
   that the command was delivered and handled — so the instruction is
   unchanged: look for the cancellation itself, not for the missing saga
   row. Step 2's `Gone`
   branch is the procedure. Three earlier revisions of this step were
   wrong in three different directions — one said "wait for one"
   unconditionally, the next said refund by hand and treat the saga
   separately, and the third read a deleted instance as proof the
   cancellation had been sent.

   An earlier version of this step said "refund the authorisation" with no
   check at all, which is a double refund whenever Payments got there first.

   It is still the step with a clock on it — an authorisation left standing
   expires or settles depending on the provider — so escalate rather than
   sit on it.

#### `cancelled_after_confirmation` — the order WAS confirmed

The aggregate confirmed and Shipping was told. That is what the code means and
it is now true by construction: §9.6 enters `Confirmed` on the aggregate's own
`OrderConfirmed`, the `Compensating` branch raises this code only when that
same event arrives there, and the despatch branches raise it only on a
`ShipmentDispatched` — which Shipping can only send for an order it learnt
about *from* `OrderConfirmed`.

**Which state raised it decides whether a saga is still live and, since #143,
whether a parcel has already left.** The row says neither, so read them off the
saga state table and off Shipping:

- **`Confirmed`, on the cancellation itself.** The branch cancels nothing and
  finalises, so the state row is gone by design and nothing is stuck.
- **`Compensating`, on an `OrderConfirmed` landing there** — a customer
  cancelling while `ConfirmOrder` was in flight, with the confirmation arriving
  afterwards. The branch does not finalise, and the instance stays until the
  stock half settles: `StockReleased`, or the ten-minute `ReleaseTimeout`. If
  an instance is there, the "deal with the saga before the money" rule above
  applies.
- **`AwaitingConfirmation` or `Confirmed`, on the despatch** — a
  `ShipmentDispatched` for an instance that had already observed a
  cancellation. `MarkOrderShipped` goes out anyway, because a parcel that left
  is a fact rather than a decision, and this row goes with it; then the branch
  finalises. **This is the raising where step 2 has nothing left to stop**, and
  the only one where the row exists because the saga was about to lose the
  instance that would otherwise have raised it.

**The stock half is the whole of that wait on the `Compensating` raising and
is not the whole of it everywhere**, which is worth stating because the rest of
that state no longer works this way. Since #124 it also holds for a payment
verdict it is still owed, and its stock exits finalise only when none is — but
the one door onto *this* raising is `AwaitingConfirmation`'s cancellation
branch, and the saga only reaches that state on a `PaymentAuthorised`. The
verdict is therefore already in before the row can be raised.

**The `Compensating` raising has a second loose end the others do not: a
`ReleaseStock` has already gone out.** That state was entered on the premise
that the aggregate had not confirmed, which was unknowable at the time —
§9.4 orders nothing between two of Ordering's own outbox rows. Nothing can
recall it, because §3.2 gives Inventory no way to be told to keep a
reservation after all. So reconcile the reservation with Inventory alongside
stopping the despatch, rather than assuming the release was correct.

**This section used to open by telling you to check the order actually
confirmed, and that check is now the code's own guarantee.** It read: the saga
enters `Confirmed` when it *sends* `ConfirmOrder`, so a cancellation beating
the command to the aggregate produces this row for an order that was never
confirmed, with the reservation stranded and a `ConfirmOrder` in the error
queue. Two of those are gone with #126 — the state waits for the
acknowledgement, and that path releases the stock. **The third was never
true**: a `ConfirmOrder` the aggregate refuses returns
`order.not_awaiting_payment`, an `Error.Rule` that `CommandConsumer` acks and
counts. It never reaches an error queue, so do not go looking for it.

**The three-day despatch timeout IS still pending on the `Confirmed`
path**, and this line said it was not. ADR-021's scheduler cannot cancel a
delayed message, so the saga's `Unschedule` is a no-op and `DespatchExpired` is
still in the broker. It is harmless — it will find no instance and be discarded
— but "no timeout is pending" is the kind of claim an on-call checks against a
queue depth. On the `Compensating` path it was never armed at all: the saga
left `AwaitingConfirmation` without ever entering `Confirmed`.

2. **Stop the despatch if it has not left — and on the despatch raising it
   already has.** Ask Shipping first; the reservation question is the same on
   every raising and is answered above. From `Confirmed` the saga deliberately
   sends no `ReleaseStock` — a reservation being picked is not one Inventory
   can safely be told to drop on a state machine's word — but §3.2 has
   Inventory releasing off `OrderCancelled` anyway, so **the restraint saves a
   message and not the stock**. That was the open question behind this step and
   it is now decided:
   [ADR-029](../backend-architecture/adr/ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)
   keeps Inventory's direct subscription — it is the only evidence a
   cancellation gives the saga, and #143's guards are built on it — and
   records the restraint as withholding a **second** instruction, not the
   first. The picked-parcel gap stays open, as Inventory's to close when
   Inventory exists. This step used to send you to release one that is normally
   already free, and to miss the real hazard: a picked parcel whose stock
   Inventory dropped on the cancellation. So the conversation with Shipping
   ends in **reinstating** a reservation if the parcel is still in the
   warehouse, not in releasing one — and where the row came from a despatch
   branch, the parcel is gone and there is nothing to reinstate.
3. **If it already shipped, this is a return rather than a cancellation** —
   which is what a customer is told if they try one:
   `order.already_shipped` now reads "an order that has already shipped cannot
   be cancelled; raise a return instead"
   ([#109](https://github.com/alexander-shamray/blueprint-backend/issues/109)).
   **The order's own state used to settle this and no longer does.** §5.4 still
   refuses to cancel a `Shipped` order, so a row here means the aggregate was
   cancelled before despatch — but on the despatch raising the parcel left
   afterwards regardless, and the `MarkOrderShipped` the saga sent was refused
   with `order.not_shippable` because the order was already `Cancelled`. So
   the order reads `Cancelled` with a parcel in transit, and only Shipping can
   tell you whether one is out.

#### `payment_authorised_during_compensation`

An authorisation landed while a cancellation was under way. **There is no
despatch to stop** — Shipping was never told, which is what separates this
code from the one above. A `cancelled_after_confirmation` row on the same
order would say otherwise, and the two can both be raised from `Compensating`.

**"While the saga was compensating" is what this said, and #143 made it too
narrow.** The condition is the money arriving while a cancellation is under
way, not which state the machine is in: `AwaitingPayment` now raises this row
too, on an authorisation that arrives after an early `StockReleased` has told
the instance a cancellation is in flight. The saga has sent no `ReleaseStock`
and entered no `Compensating` on that path — Inventory released off the
customer's `OrderCancelled` directly — and what the row records is the
`ConfirmOrder` that was withheld instead.

**Compensation started one of several ways and the row does not say which**: a
customer cancellation in `AwaitingStock`, in `AwaitingPayment` or in
`AwaitingConfirmation`, a declined payment, or the fifteen-minute payment
timeout — or, on the #143 raising, it had not started at all. The timeout is
the one worth ruling out first, because it means Payments answered late rather
than a customer changing their mind. The `AwaitingConfirmation` door is the
only one that can later add a `cancelled_after_confirmation` row beside this
one.

**An authorisation late enough produces no row at all, and that is this
code's bound rather than a gap in it.** `Compensating` stops waiting when the
payment timeout expires — thirty minutes from `AuthorisePayment` on the
timeout door, fifteen on the cancellation door — and an authorisation arriving
after the instance has gone correlates to nothing. Since #124 that is not
silent: `PaymentAuthorised` faults on a missing instance without qualification,
because Payments produces it and it can therefore never be Ordering's own echo
— since #123 an `OrderCancelled` faults too, but only for the origins this
service cannot account for — so it lands in the error queue
[§13.6](../backend-architecture/13-observability.md) pages on and surfaces
through [`error-queue.md`](error-queue.md) instead of this table. It is the
same money problem reaching you by the other alert, and the procedure below
applies with step 2 answered in advance — there is no instance, and there
will not be one.

**This row is raised mid-wait, which is what lets the instance still exist —
but the wait is short and the alert is not.** The branch that writes this row
clears the payment verdict on its way, so the stock half is the only thing
left holding the instance: `StockReleased` and the ten-minute `ReleaseTimeout`
both land well inside the hour this alerts on, and the ordinary case is a
finalised saga. An earlier version of this section asserted a live instance in
its heading and then explained two paragraphs down that there would not be one.

**The #143 raising is mid-wait in a different state and is bounded by a
different timeout.** In `AwaitingPayment` the payment verdict **has**
settled — the authorisation is what raised this row, and the branch cleared
`PaymentVerdictOutstanding` on the way — and the review command **has** gone
out. Do not go looking for a payment response: it already arrived. What was
withheld is the `ConfirmOrder`, and what has not happened is the saga
consuming its own copy of the `OrderCancelled` Inventory already acted on.
The fifteen-minute `PaymentTimeout` armed when `AuthorisePayment` went out is
what ends that wait — it compensates exactly as it would have. Minutes again,
against an alert measured in hours, so the ordinary case is still a finalised
saga. **So step 2 is a branch, not an
instruction, and `CurrentState` is half of it**:

2. **Look for the instance**, by `CorrelationId = OrderId` in the saga state
   table, and read `CurrentState` and `CancellationObserved` with it.
   - **Gone** — the ordinary case, but it does **not** by itself prove the
     stock came back. The stock half settles two ways — `StockReleased`, and
     the ten-minute `ReleaseTimeout`, which gives up on it and raises a
     `stock_not_released` row — and with the payment verdict already cleared
     by this row's own branch, either one finalises the instance.

     **`StockReleased` is not proof that a reservation was released**, and
     this branch read "the reservation actually released" until
     [ADR-024](../backend-architecture/adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)
     made that false. It reports a postcondition — Inventory holds no stock
     for this order — so it is published for a release that found nothing and
     for a reserve refused against the tombstone, neither of which freed
     anything. As evidence it is worth exactly "Inventory says no stock is
     held", which is what an operator needs and is a weaker claim than a
     state change. **Check for that second row before deciding
     stock needs nothing** — if it is there, the reservation may still be
     held and [that section](#stock_not_released) is the procedure.

     **Its absence proves nothing either, and the reason has changed.** It
     used to be permanent loss: the `ReleaseTimeout` exit buffered its
     `FlagOrderForReview` in the same in-memory outbox as its `CancelOrder`
     and finalised in the same transaction, so #128's window lost both
     sends together — a reservation still held, no review row naming it,
     and no cancellation.
     [ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
     closed that window: the exit's sends are written to
     `ordering.OutboxMessage` in the transaction that finalises the
     instance, so they are exactly as durable as the finalisation.

     **What is left is delivery, and it is still not an answer.** The row
     appears only once `FlagOrderForReview` has reached
     `ordering-commands` and its handler has run, which is after the saga's
     transaction — so a missing row can mean the command is in flight, or
     that it is parked in the error queue
     ([`error-queue.md`](error-queue.md)). **Ask Inventory whether the
     reservation is still held** rather than reading a missing row as an
     answer. It is one query, and it is the only check that tells
     "released" apart from "not reported yet".

     **This page corrected the money half of that inference one revision
     ago and left the stock half standing six lines above it.** Both read an
     absence as evidence, both were refuted by the same crash window, and
     only one was fixed — which is the failure this repository keeps paying
     for: the fix goes where the finding pointed, not where the shape
     recurs. **Closing the window did not retire the lesson**: both halves
     are still inferences from an absence, and what changed is what an
     absence means, not whether it means anything.

     **A gone instance is proof that the cancellation was *staged*, and
     not that it was sent** — and this branch has now been wrong in both
     directions, which is why the mechanism is written out rather than the
     conclusion. It first said a gone instance proved the cancellation was
     sent.
     [#128](https://github.com/alexander-shamray/blueprint-backend/issues/128)
     made that false in the strongest way: `SetCompletedWhenFinalized`
     deletes the row inside the transaction that commits the exit, while
     `UseInMemoryOutbox` flushed the buffered `CancelOrder` only after the
     consume pipeline returned, so a crash between the two left no
     instance and no `CancelOrder` ever sent.
     [ADR-032](../backend-architecture/adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
     put those sends in that same transaction. The two now commit
     together, so no instance means the `CancelOrder` was durably
     *staged* — written to `ordering.OutboxMessage`, MassTransit's table,
     singular, not §9.4's `ordering.OutboxMessages`.

     **Do not expect to find the row, though.** MassTransit's outbox
     middleware deletes an `ordering.OutboxMessage` row once the message
     has reached the transport, so on the ordinary path the command was
     staged, sent and the row removed long before anyone looked. **An
     empty result there is the normal case and settles nothing.** A row
     still sitting there is the informative find: it says the delivery has
     not happened yet.

     **What is left between staged and handled is delivery**, the same gap
     as the stock row above. **So still check for the cancellation itself,
     not for the absence of a saga.** The order's own state, or an
     `OrderCancelled` row in `ordering.OutboxMessages`, is what settles
     it. What changed is where an unanswered case points — at a delivery
     that has not happened yet or an error queue, rather than at a message
     nobody will ever send.

     **Cancelled, and no `PaymentRefunded`** — the automatic path fired and
     produced nothing, which is a failure on Payments' side rather than a
     missing instruction on Ordering's. Check that service's error queue
     and replay the consumer, because one that succeeds on retry voids the
     authorisation and duplicates anything paid by hand. Refund manually
     only if the authorisation cannot wait for that, record it, and own the
     reconciliation — the same terms as every other manual refund here.

     **Not cancelled** — the order is still open and the money is still
     authorised. **What this no longer means is that nothing further is
     coming**, which is what the branch said while #128's window was open
     and the sender really had stopped existing. ADR-032 staged the
     `CancelOrder` with the finalisation, so what is outstanding is its
     delivery or its handling: a row still in `ordering.OutboxMessage`, or
     a fault on `ordering-commands`
     ([`error-queue.md`](error-queue.md)). **Recover the cancellation
     rather than the money**: work whichever of those two it is, and if
     neither shows anything, send `CancelOrder` (§11.4's endpoint), which
     publishes `OrderCancelled` and gives Payments the void it consumes.
     `Order.Cancel` returns without raising on an order already cancelled,
     so a hand-sent one that races the staged copy costs nothing. Refunding
     by hand here is the same duplicate the live-instance branch warns
     about, arriving by a different route.

     **`PaymentRefunded` already there** — the workflow finished. Nothing
     to do.

     **This branch has now been corrected six times, and three of those
     corrected the previous correction.** Round 25 deleted step 1's refund
     instruction because it was wrong for the live branch; round 26 put it
     back here, where it is right, and asserted that a gone instance means
     the cancellation was sent; round 30 found that assertion refuted by
     this branch's own #128. The pattern is worth the lines it costs: every
     revision reasoned from *what the saga does* and each was falsified by
     *what the saga does when it stops halfway*.
   - **Still there in `Compensating`** — neither stock settlement happened,
     which is its own incident. With the payment half already answered by
     this row's own branch, `StockReleased` and its timeout are the only
     things left, so both went missing. **Leave the reservation alone**: the
     machine is waiting on `StockReleased` and will cancel the order when it
     arrives, and releasing by hand races it.
     **The same is true of the money**, and it is the less obvious half:
     that cancellation publishes the `OrderCancelled` Payments voids off,
     so a manual refund now is one the automatic path will duplicate when
     the instance is unstuck. Fix the saga first.
   - **Still there in `AwaitingPayment`, with `CancellationObserved = 1`** —
     the #143 raising, and a different incident from the one above. Nothing
     miscarried: the instance withheld `ConfirmOrder` on purpose and is
     waiting for its own copy of a cancellation the broker has already
     carried to Inventory. **So work it as a delivery problem on
     `ordering-fulfilment-saga`, not as a money one** —
     [`stuck-saga.md`](stuck-saga.md)'s manual-compensation section says
     where that copy usually is. Do not release stock: Inventory released
     off the cancellation, which is what set the flag. Do not refund: the
     `OrderCancelled` is published, so Payments voids off it, and a manual
     refund duplicates that on the same terms as the branch above.
3. **A live instance at this age has already missed a timeout — do not
   wait for it again.** This row alerts at one hour, and every wait that can
   be holding the instance is far shorter: ten minutes on `Compensating`'s
   stock half, fifteen on a payment verdict, and fifteen on the
   `PaymentTimeout` still armed behind the `AwaitingPayment` raising. So an
   instance still here means a timeout never arrived, which is a scheduler
   incident rather than a slow peer: [`stuck-saga.md`](stuck-saga.md) is the
   procedure, and **the saga-age alert will have fired too** — the saga
   predates this row, so at one hour it is over that alert's threshold as
   well.

   **This step used to reason from `ReleaseTimeout` alone, and that is no
   longer sufficient anywhere but here.** Since #124 `Compensating` also
   holds an instance open for an outstanding payment verdict, so in general
   a live instance past ten minutes is not yet a scheduler fault — it may be
   the machine correctly waiting on Payments, for up to fifteen minutes more.
   On *this* row it still is a scheduler fault, because the branch that
   raised the row answered the payment half, which leaves the release
   timeout as the only thing that can have gone missing. At one hour the
   distinction is moot in either direction: both waits have long expired.
   Read `StockReleaseSettled` and `PaymentVerdictOutstanding` on the state
   row if you want the answer rather than the argument.

   Two earlier versions of this step were wrong in opposite directions: the
   first said the saga-age alert would fire in the ordinary case, and the
   correction said it would not fire at all. It does not fire for a finalised
   saga and it does for a live one, which is the only case this step is about.

## Resolving

**Delete the row.** That is the entire mechanism, and the absence of a
`ResolvedAt` is deliberate: a nullable timestamp nothing sets is an alert that
fires once and never clears, and "resolved" and "gone" are the same state for a
queue.

```sql
DELETE FROM ordering.OrderReviews
WHERE OrderId = @OrderId
    AND Reason = @Reason;
```

Delete one `(OrderId, Reason)` pair at a time. A bare `DELETE` over the table
clears the alert and loses the queue, and there is no undo — the audit trail of
what was escalated lives in the event history, not here.

**Do not delete to silence the alert.** The row is the only thing telling anyone
this order needs a human; the age in the alert is a service-level target for
working the queue, not a nuisance to clear.

## If the queue is growing

Many rows with the same reason is not twelve independent incidents — it is one
upstream fault. `not_despatched` in bulk means Shipping stopped despatching or
stopped publishing; `stock_not_released` in bulk means Inventory is not
consuming `ReleaseStock`. Work the upstream service and the queue drains behind
it, but the rows still need deleting — nothing removes them automatically.

**`not_confirmed` in bulk is the sharpest case of this rule and the only one
where "upstream" is this service.** The wait it comes from bounds Ordering
consuming its own `ConfirmOrder`, so a spike means the outbox stopped, the
`ordering-commands` queue is not being drained, or a rollout is stranding
acknowledgements ([#131](https://github.com/alexander-shamray/blueprint-backend/issues/131)).
Every one of those is a single fault producing a row per paid order, and every
one is fixable here rather than by another team. Check the queue depth before
the rows.

`cancelled_after_confirmation` is the one that does **not** follow that rule:
every path to it starts with an `OrderCancelled`, so its upstream is customers,
a spike is a product or pricing signal rather than a dependency, and there is
no service to fix. Look at what confirmed orders are being cancelled *for*
before treating it as an incident. **That holds for every state that raises
it** — the `Compensating` one is reached only through `AwaitingConfirmation`'s
cancellation branch, and the despatch branches raise it only for an instance
that observed a cancellation, which is the same event reaching Inventory
instead of the saga. In each the extra ingredient is a race rather than a
fault.

**`payment_authorised_during_compensation` follows BOTH rules, and this section
used to file it with the customer-driven one.** It is raised when an
authorisation lands while the saga is compensating, and compensation starts on
a customer cancelling, on a declined payment, or on the **fifteen-minute
payment timeout**. That last one is an upstream fault wearing a customer-shaped
code — a PSP slower than the timeout that then authorises anyway. So a spike
here is a Payments latency signal until the orders say otherwise, and the cheap
discriminator is whether the orders carry a customer cancellation at all.

**The #143 raising files with the customer-driven half, and sharpens that
discriminator rather than blunting it.** An `AwaitingPayment` instance raises
this row only because it observed a cancellation, so those orders carry one by
construction — and a spike of *them* is not a Payments signal at all but
customers cancelling inside the authorisation window. `CurrentState` on the
saga rows separates the two populations while the instances still exist, which
is another reason to look before the hour has passed.
