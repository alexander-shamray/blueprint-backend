# ADR-029 — Inventory releases on the cancellation, not on the saga's word

**Decision.** Inventory goes on consuming `OrderCancelled` directly, as
[§3.2](../03-bounded-contexts.md) has always given it, and releases the stock it
holds for that order **whatever state §9.6's saga is in**. `ReleaseStock` does
not become the only trigger. The saga's `Confirmed` branch continues to send no
release, and that restraint is now documented for what it is: it withholds a
**second** instruction, not the first.

**Why.** [#141](https://github.com/alexander-shamray/blueprint-backend/issues/141)
asked whether Inventory should decline to release for an order it knows reached
`Confirmed`, and named three sketches. Making `ReleaseStock` the only trigger
was called the cleanest and largest. It is neither, once two things beside it
are read together.

**The second producer is a safety net rather than a duplication.** With
`ReleaseStock` as the only trigger, a customer's stock comes back only if a
saga instance exists to send the command. §9.6 finalises down several branches,
some of them before any despatch has been arranged, and a cancellation arriving
after any of them would then release nothing at all — the reservation held
until a person noticed. Removing the direct subscription buys tidiness in the
diagram and pays for it with a single point of failure on the one obligation
the customer can see.

**And it is the only evidence a cancellation gives the saga.**
[#143](https://github.com/alexander-shamray/blueprint-backend/issues/143)
turns on a `StockReleased` arriving in a state that sent no release: that
arrival *proves* a cancellation reached Inventory, and it is what four states
now record on the instance — `CancellationObserved`, which is
[ADR-025](ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)'s
first rule applied to a fact rather than to an obligation — so a forward step
can be withheld. It exists only
because Inventory consumes the event directly. So the two issues cannot be
settled independently in the direction they were filed — taking #141's option 2
would delete the mechanism #143's fix is built on, and leave the saga with no
way to know a cancellation is in flight until its own copy lands.

> **Two open questions were being weighed independently and one decides the
> other.** #141 ranked its sketches by cost while #143 was still open beside
> it; answering #143 removes the cheapest of them from the table entirely. The
> same shape closed #125, and it is worth the second recording: before ranking
> options by cost, ask whether each survives its neighbours being settled.

**What the restraint is actually for, then.** Reaching `Confirmed` means a
despatch may be moving, and a reservation being picked is not one Inventory can
safely be told to drop on a state machine's word. That argument is about the
*command*, and it survives: the saga does not issue one. It was never an
argument about the reservation surviving, and three documents implied it was
until the PR that wrote `Confirmed`'s fourth absorption corrected them.

**Consequences.**

- **A picked parcel's reservation is released, and no mechanism prevents it.**
  §9.6 raises `cancelled_after_confirmation` and an operator reinstates the
  reservation by hand if the parcel is still in the warehouse;
  `docs/runbooks/order-review.md` step 2 owns that procedure and already says
  so. This ADR does not close that gap — it records that the gap is Inventory's
  to close when Inventory exists, and that closing it needs Inventory to know
  the order was confirmed.
- **The closing move is available and is not taken here.** #141's sketch 1 —
  Inventory declining to release for an order it has seen confirmed — needs
  `OrderConfirmed` in Inventory's Consumes column, which §3.2 does not give it
  and no chapter asks for. Adding a subscription to a service with no code, for
  a case no runbook has yet worked, is a decision better taken by whoever builds
  it against a real picking process.
- **One cancellation keeps two independent routes to `StockReleased`**, and
  ADR-024's guarantees are what make that safe rather than racy. This decision
  depends on that one; neither is correct alone.
- **Nothing enforces any of it until Inventory is built.** Like ADR-024, this
  is a commitment on a service that does not exist, which is the cheapest moment
  to take it and the reason it is written down rather than assumed.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
