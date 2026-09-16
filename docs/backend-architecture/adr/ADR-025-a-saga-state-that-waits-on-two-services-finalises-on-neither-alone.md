# ADR-025 — A saga state that waits on two services finalises on neither alone

**Decision.** Where a state can be waiting on more than one participant, what
is outstanding is recorded **on the instance** and the state finalises only
once every obligation has been discharged. A state name may carry one such
fact; it cannot carry the rest.

Three rules follow, and [§9.6](../09-messaging.md)'s `Compensating` is the worked
example:

1. **The obligation is recorded where it is incurred**, not inferred later.
   `PaymentVerdictOutstanding` is set in the activity that sends
   `AuthorisePayment`, so it commits with the transition that creates the
   debt.
2. **A timeout ends the wait, not the obligation.** A participant that has not
   answered has not answered *yet*, so a timeout is a bound on how long the
   instance is held and never a substitute for the answer. It is the arrival
   that discharges the obligation; the timeout only stops asking.
3. **Every exit asks about the other halves.** Either answer may land first,
   so `Finalize` is conditional on the join rather than attached to whichever
   transition was written first.

**Why.** `Compensating` is reached from `AwaitingPayment` with
`AuthorisePayment` already sent and unanswered, so Inventory and Payments both
owe it an answer and [§9.4](../09-messaging.md) orders nothing between them. Both
exits finalised on the stock half alone, so whenever Inventory answered first —
the **expected** interleaving, since a prompt warehouse and a slow PSP is the
ordinary shape rather than the degenerate one — `SetCompletedWhenFinalized`
deleted the instance and the authorisation still in flight correlated to
nothing. It was consumed cleanly: no transition, no fault, and no
`payment_authorised_during_compensation` row, which is the escalation §9.6
provides for precisely that case. The money moved and nobody was told
([#124](https://github.com/alexander-shamray/blueprint-backend/issues/124)).

**The state could not answer the question and that is the general fault.**
`Compensating` is entered five ways and whether a verdict is owed differs by
route — nothing from `AwaitingStock`, already answered from
`AwaitingConfirmation`, and from `AwaitingPayment` it depends on whether a
decline, a timeout or a cancellation brought it there. A single state name
compresses all five into one, so the machine had thrown away the fact its exit
needed before the exit ran.

> **The alternative was a sixth state and it buys nothing here.** A
> `Compensated` state meaning "stock settled, still waiting on a verdict"
> would make the same distinction in the state name, at the cost of a new
> state, a new schedule and a new timeout type — and it would have to be
> paired with a seventh for the mirror case, where the verdict lands first and
> the stock half is outstanding. Two booleans and one join express what four
> states would enumerate. The rule is worth stating in that direction: a state
> per *combination* of outstanding obligations is a product, and the instance
> is where a product belongs.

**Consequences.**

**A cancelled order is held for as long as a verdict can still arrive, and
that is bounded rather than open.** The fifteen-minute payment wait armed with
`AuthorisePayment` is deliberately **not** unscheduled by the cancellation
branch, so it runs on into `Compensating` and ends the wait there; the timeout
door re-arms it once, giving one further window. The longest an instance is
held is therefore thirty minutes from `AuthorisePayment`, which is inside
[§13.6](../13-observability.md)'s one-hour unfinalised-saga alert with room to
spare. **A join with no bound would have traded a silent loss for a pager**,
which is not a trade worth making.

**The bound raises no review row, and that is a decision.**
[§3.2](../03-bounded-contexts.md) has Payments consuming `OrderCancelled`, so an
authorisation abandoned on a cancelled order is what *should* happen. A row on
the timeout would escalate the healthy path — one per cancelled order the PSP
correctly dropped — and the escalation belongs where money actually moved.

**A decline stops being ignorable.** It moves no money and still raises
nothing, but it is an **answer**, so ignoring it held the instance open until
the wait expired for a verdict that had already arrived. The general form is
that a catch-all `Ignore` is only safe for arrivals that carry no information
the machine is waiting for.

**The tail past the bound is answered by a fault rather than by the machine.**
An authorisation landing after the saga has stopped waiting finds no instance,
so §9.6 gives `PaymentAuthorised` `OnMissingInstance(m => m.Fault())` and it
reaches the error queue §13.6 pages on with the message retained. That is
sound for this event and for no other in the machine: Payments produces it, so
unlike `OrderCancelled` — Ordering's own echo — or `StockReleased`, which
ADR-024 has answered for every release including a no-op one, it can never be
a routine arrival at a finalised instance. **Recovering the review row instead
would mean persisting the obligation outside the saga**, which is a bigger
change than this one and is not taken here.

> **The contrast with `OrderCancelled` has since been narrowed, and this
> paragraph is left as written.** It reads that event as Ordering's own echo
> without qualification, which was true of every arrival when this ADR was
> taken and is true of only some of them now:
> [#123](https://github.com/alexander-shamray/blueprint-backend/issues/123)
> gave the contract an `Origin` field, so §9.6 asks rather than assuming and
> faults for anything it cannot account for. The distinction this ADR draws —
> **provenance, not timing** — is what survived and is what the field makes
> mechanical; only the claim that provenance was unavailable for a
> cancellation has moved. Recorded here rather than edited above, because an
> ADR is superseded and never rewritten.

**The instance carries facts as well as obligations, and the second use is
§9.6's `CancellationObserved`.** An early `StockReleased` absorbed in a state
that sent no release proves a cancellation reached Inventory
([ADR-029](ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)),
and the saga records that where it observes it and guards its forward
transitions on it. Nothing is outstanding and no exit joins on it, so rule 1 is
the half that generalises: the place a fact a state name cannot carry belongs
is the instance, and only rules 2 and 3 are about waiting.

**Nothing enforces the rule beyond §9.6.** No gate reads a state machine for
states that wait on two participants, and the only one this platform has is
the one above. What stands in for a gate is the structural test that
partitions `Compensating`'s declared next-events, which fails when a branch is
added without being argued — it caught nothing here, because the join was
written deliberately, but it is what a later state waiting on two things would
run into first.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
