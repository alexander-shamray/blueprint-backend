# ADR-024 — A release answers for the order, not for the reservation

**Decision.** `ReleaseStock` is idempotent over **ordering** as well as over
repetition, and Inventory owes two guarantees for it:

1. **It always publishes `StockReleased`.** The event reports the command's
   postcondition — no stock is held for this order — and not a state change, so
   a release that finds nothing to release publishes exactly as one that frees a
   reservation does.
2. **A release for an order whose `ReserveStock` has not arrived is
   remembered**, and the `ReserveStock` that follows it is refused rather than
   creating a reservation nobody is waiting for. **The refusal answers with
   `StockReleased`**, because that is what it establishes — no stock is held
   for this order — which is the same postcondition guarantee 1 reports and
   needs no new member in the vocabulary.

> **`StockReservationFailed` is the obvious answer and cannot carry it.** That
> event means an out-of-stock decision and requires `UnavailableProductIds`
> ([§9.1](../09-messaging.md)); a reserve refused because the order was already
> released has **no** unavailable products, so the producer would have no
> truthful payload and every consumer would read a stock shortage that did not
> happen. Answering with `StockReleased` follows from guarantee 1 rather than
> being a second decision: once the event reports a postcondition instead of a
> state change, the refused reserve and the no-op release are reporting the
> same fact. The alternative — a new event, or a `Reason` on
> `StockReservationFailed` — is a [§9.2](../09-messaging.md) contract addition
> this ADR does not need.

Both are stated in [§3.2](../03-bounded-contexts.md) beside Inventory's row. They
are commitments on a service that does not exist yet, which is the cheapest
moment to take them.

**Why.** [§9.4](../09-messaging.md) orders nothing between two deliveries, and
[§9.6](../09-messaging.md)'s saga sends `ReleaseStock` from every compensating
transition it has, without ever knowing whether the `ReserveStock` it undoes has
been handled. **The count is deliberately not given**: it read "four" here for
as long as the machine had five, because three *states* send a release and four
absorb an early one, and a third figure between them is exactly the kind nothing
recomputes.
Nothing in the sender can establish that, so the guarantee has to be the
receiver's.

**A conditional acknowledgement pages a human on the ordinary case.**
`Compensating`'s **stock half** settles exactly two ways: `StockReleased`, and
a ten-minute `ReleaseTimeout` that raises a `stock_not_released` review. Under
the other reading — a release of nothing has nothing to report — every no-op
release leaves through the timeout.

> **This paragraph said "`Compensating` has exactly two exits" and the state
> outgrew it.** [#124](https://github.com/alexander-shamray/blueprint-backend/issues/124)
> made that state a join: it can also be waiting on a payment verdict, so
> `Finalize` is conditional and a stock answer no longer ends the instance by
> itself. Nothing in this ADR turns on that. What the argument needs is that
> the stock half has two settlements and that one of them escalates, which is
> unchanged — the wider claim was a convenience the sentence did not require,
> and it is the kind that goes stale one state machine change later.

`StockReservationFailed` reaches `Compensating` having *proved* the reservation
was never taken, so that is not a corner case but a routine one, and the review
row it raises names a stranded reservation that never existed. A contract whose
normal path escalates to on-call is the wrong contract.

**The second guarantee is the only thing that closes the stranding.** With the
first alone, a release handled before its reserve is a no-op that publishes,
the saga finalises on it, and the `StockReserved` that follows correlates to no
instance and is discarded — a reservation held for an order that is cancelled,
with nothing raised anywhere
([#125](https://github.com/alexander-shamray/blueprint-backend/issues/125)).
The saga cannot close that from its side: by the time the late `StockReserved`
exists the instance is gone, so any transition written for it is a transition
nothing can reach. The tombstone moves the reconciliation to the only place
that still has both facts.

> **The alternative was a second `ReleaseStock` from `Compensating`, and it is
> unreachable rather than merely weaker.** Sending one on `StockReserved`
> was the cheap fix while the first guarantee was unstated; with it stated, the
> no-op release has already answered and finalised the instance, so the branch
> that would send the second release is never entered. A fix whose precondition
> is the defect it shares a release with is not a fix.

**Consequences.**

**Inventory carries a tombstone, and it is not free.** A release for an unknown
order is a row that must persist long enough to meet the `ReserveStock` it is
waiting for, and be reaped afterwards. That is the same retention question
[§9.5](../09-messaging.md)'s inbox already answers for itself, and Inventory's PR
inherits it rather than inventing it.

> **The bound is the order's lifetime, not a figure off the retry ladder**, and
> quoting the ladder is how this paragraph first got it wrong. It cited 635
> seconds as though that were the far end; 635 is the *seventh* failure's
> cumulative wait, `OutboxDispatcher.MaxAttempts` is **10**, and the eighth and
> ninth land near 1,275 and 2,555 seconds — some three quarters of an hour
> before the row may be reaped, with broker backlog on top of that and no bound
> of its own. A tombstone reaped on the ladder's midpoint lets a late
> `ReserveStock` recreate exactly the reservation this ADR exists to prevent,
> which makes the retention a correctness property rather than housekeeping.
> **A horizon derived from one term of a ladder is a horizon that expires
> mid-ladder.**

**The saga may absorb a `StockReleased` that overtakes its own cancellation.**
Inventory consumes `OrderCancelled` directly ([§3.2](../03-bounded-contexts.md)),
so one publication starts two races to the saga's queue. Absorbing the early
arrival used to lose it — the saga would then wait out a release it had already
discarded — and under the first guarantee it does not, because the saga's own
`ReleaseStock` is answered whatever Inventory did with the event. `Ignore` is
sound *because of this ADR* and not on its own, which is stated at each of the
three sites that send one.

> **The fourth site does not rest on this ADR, and that is the interesting
> half.** `Confirmed` absorbs an early release too, and deliberately sends no
> `ReleaseStock` — a reservation being picked is not one Inventory can be told
> to drop on a saga's word. So there is no exit for the discarded copy to have
> come from, and nothing there depends on the command being answered. Writing
> it out is worth doing anyway, because the arrival is legitimate and an
> unwritten one faults; the argument for it is simply a different argument, and
> collapsing the four into one reason is what an earlier revision of this
> paragraph did.

> **Whether the event should reach Inventory at all is
> [ADR-029](ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)'s
> question rather than this one's, and it keeps it.** This ADR takes the direct
> subscription as given and makes the early arrival harmless; that one asks
> whether to delete it and declines, because a cancellation that finds no
> instance to send a `ReleaseStock` would otherwise release nothing, and
> because the early arrival is the only evidence a cancellation gives the saga.
> So the absorptions this ADR names, here and under *Nothing enforces it*
> below, have since stopped being an `Ignore`: each records the arrival on the
> instance, and forward transitions are guarded on what it recorded (§9.6).
> What this ADR establishes — that the discarded copy costs the saga nothing —
> is unchanged; what the copy is *for* is not.

**A `stock_not_released` review now means what it says.** It is raised only when
a release genuinely never completed, so
[`order-review.md`](../../runbooks/order-review.md)'s procedure no longer has to
open with "check whether there was ever a reservation".

**Nothing enforces it until Inventory is built.** No gate can hold an unwritten
service to a contract, and this repository's own rule is that a list of things
known to be missing needs something asserting they are still missing. What
stands in for it here is that every place the machine leans on it says so at
the line: `AwaitingStock`, `AwaitingPayment` and `AwaitingConfirmation`'s
`When(StockReleased)`, the cancellation branches those three states absorb
for, and `Compensating`'s `Ignore(StockReserved)` and
`Ignore(StockReservationFailed)`. So an Inventory built to a different rule
contradicts a paragraph rather than failing silently. **The sites are named
rather than counted** for the reason the Why section declines a count — a
figure over a set the next branch can add to is one nothing recomputes.

> **The first three were spelled `Ignore(StockReleased)` until #143, and the
> spelling is updated here rather than left standing.** An ADR is superseded
> and never rewritten, and nothing above has been: the decision, its two
> guarantees and every argument for them are untouched. What changed is an
> **index** — this paragraph exists so an Inventory implementer can find the
> lines that lean on the ADR, and a name that greps to this file and not to
> the machine defeats the only job it has. The callout above records that
> those absorptions stopped being an `Ignore`; this is the same fact where
> somebody would look it up.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
