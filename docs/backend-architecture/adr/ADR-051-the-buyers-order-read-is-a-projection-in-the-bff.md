# ADR-051 — The buyer's order read is a projection in the BFF

**Decision.** [§10.7](../10-api-gateway.md)'s two buyer-facing routes —
`GET /bff/v1/orders` and `GET /bff/v1/orders/{id}` — are served from a
projection `Web.Bff` owns, fed by integration events that already exist:
Ordering's `OrderPlaced`, `OrderConfirmed` and `OrderCancelled`, Payments'
`PaymentAuthorised`, `PaymentDeclined` and `PaymentRefunded`, Shipping's
`ShipmentDispatched` and `ShipmentDelivered`, and Catalog's `ProductPublished`
for the product names a line carries only an id for. The read makes **no
synchronous call**, and **Shipping gains no read endpoint**.

**Why.** [§9.7](../09-messaging.md) permits the obvious alternative rather
than forbidding it: the budget "is depth, not fan-out", and a BFF calling
Ordering, Payments and Shipping concurrently is one hop deep. So the case
against fan-out is not the rule. It is what fan-out costs here, and the cost
is paid twice.

It is paid in availability first.
[ADR-017](ADR-017-one-synchronous-hop.md) opens with four services at 99.9%
giving 99.6%, and this is the page a buyer reloads while waiting for goods.
§9.7 states the same figure as a ceiling one paragraph on: beyond about three
calls "the data should be arriving by event and being projected locally
instead". This read is at exactly three.

It is paid in chapters second, and that is the larger bill. Shipping has no
API in four places — [§3.2](../03-bounded-contexts.md)'s contract row accepts
no commands, [§4.1](../04-solution-structure.md) gives it `Worker` and not
`Api`, [§13](../13-observability.md) says twice that it has no public API at
all, and [§15](../15-cicd-deployment.md) gives it "the same chart minus the
Service and the Ingress" — and `tools/new-service` refuses the name outright.
A synchronous read of a shipment therefore costs four chapters, a chart and
the scaffold, and it costs them the day before Shipping's own spec is
written. The projection costs one host a consumer.

Two facts settle what those arguments leave open. **`ShipmentDelivered`
reaches no consumer in Ordering** — §3.2 gives it none, and
`OrderStatus.Delivered` accordingly has no transition and no chapter gives it
one — so a reader that consumes the event itself is the only reader that can
ever show a delivered order. And **Payments' decline reasons are the
provider's codes**, an open set of which
[ADR-049](ADR-049-a-cancellation-payments-has-recorded-declines-the-authorisation-that-follows.md)
names the single member Payments owns; a closed buyer vocabulary has to map
them rather than pass them on, and the mapping belongs where the screen is.

**Consequences.** **The BFF stops being stateless**, which is the price and
very nearly the whole of it. It gains a broker connection, consumers, a
schema and a migrator, so
[§2.2](../02-architecture-at-a-glance.md) draws it today with one edge it
will not have, [§13.5](../13-observability.md) names it as one of the two
hosts owning no readiness dependency,
[§14.1](../14-local-development.md) gives it no connection string, and
[ADR-036](ADR-036-the-broker-has-a-per-service-identity.md) no broker
account. **The pull request that implements this read amends each of them**,
the way [ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
left a delivery address to Shipping's own. This record decides the shape so
that Shipping's spec can be written against it; nothing here is built until
Shipping publishes the two events it is owed.

**Staleness is exposed rather than hidden**, which [§6.6](../06-cqrs.md)
requires — but not by §6.6's remedy. There a detail endpoint reads the write
model while the list serves the projection. The BFF has no write model to
read, so both responses carry the instant the projection was current at and
the client renders it. A buyer who cancels and reloads may see the order
un-cancelled for the projection's lag, which is the trade ADR-017 calls
intended, and §10.7's `cancellable` is what keeps it from becoming an error
the buyer meets.

**§6.6's escalation trigger is no longer Ordering's.** It names the customer
order history screen, and that screen is this one. What §6.6 demonstrates is
unchanged and still the chapter's subject — a level-1 slice escalated in
place — but the screen that triggers it moved one host out, and §6.6 now says
so rather than naming a screen it no longer serves.

**§3.2's "no public write API" becomes "no public API"** for Shipping. That
is what §13 and §15 have said all along while citing §3.2 for it, so the
discrepancy predates this decision; what the decision adds is the reason the
stronger reading is now safe to write down, which is that the one consumer
who would have needed the weaker one no longer does.

**A second copy of the truth**, with §6.6's trap attached to it: its own
bugs, its own monitoring, and a rebuild script in source control from the
first day rather than the day it is first needed. This copy is worse than
Ordering's in one respect worth stating plainly — it is fed by four services'
streams rather than by one database's tables, so a rebuild replays from the
broker's retention window and not from anything a reader can select from.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
