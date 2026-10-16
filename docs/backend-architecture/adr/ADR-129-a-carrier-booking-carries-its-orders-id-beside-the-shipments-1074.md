# ADR-129 — A carrier booking carries its order's id beside the shipment's (#1074)

**Decision.** `POST /v1/shipments`'s body is `shipmentId`, `orderId` and the
address. `orderId` is the order's id, written by
`HttpCarrierGateway` from `BookingRequest.OrderId`, and the body stays pinned
whole by `HttpCarrierGatewayTests`, so a fourth field fails there before it
reaches a carrier. The carrier simulator's mappings match on `postalCode`
alone and ignore it, and `deploy/compose/carrier-simulator/README.md` names it.
`docs/personal-data.md`'s processor row for the carrier gains the order's id.
**Why.** `shipmentId` is Shipping's own `Guid.CreateVersion7()` and no read the
BFF offers exposes it, so a carrier's log held an entry per booking that
nothing outside Shipping's database could tie to the order that caused it. The
console's carrier step (blueprint-admin #67) told two orders to one postal code
apart only by the time after each was placed, which is wrong for two at once. A
real carrier's support is likewise asked about a booking by the id its
customer knows.
**Consequences.** The carrier now holds a key that joins a postal address to an
order. It is not a customer id and no name travels with it, and
[the personal-data register](../../personal-data.md)'s `shipping.Shipments` row
already says an order id identifies a parcel and nobody until joined to its
order; the push provider is shown the same id. An erasure still cannot reach
the carrier's copy, as it could not reach the address. An adopter's carrier
that refuses a field it does not know must have the adapter drop this one, and
the pinned shape is where that edit shows.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
