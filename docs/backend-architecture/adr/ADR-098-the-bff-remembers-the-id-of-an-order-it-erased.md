# ADR-098 — The BFF remembers the id of an order it erased

**Supersedes.** The last residual in
[ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)'s
Consequences, which accepted that a shipment or payment event for an erased
order leaves a row with no owner.
**Decision.** When the BFF deletes an order row for an erased customer, in the
erasure's own delete or in ADR-097's check after a write, it records the
order's id and the time in `bff.ErasedOrders` in the same transaction.
`ShipmentDispatched`, `ShipmentDelivered`, `PaymentAuthorised` and
`PaymentRefunded` name an order and no customer, so after each of their writes
the projection deletes the row when it has no owner and its id is in that
table. The table holds an id and a time, has no retention window, and is left
alone by `tools/bff-replay`'s `--reset`, as `PersonalDataErasures` is.
**Why.** ADR-097 could not ask about a customer these four events do not name,
so a rebuild inside the outbox window, or a live event after the erasure,
inserted an order row with a tracking number and no `CustomerId`, which the
erasure's `WHERE CustomerId = ...` never reaches. The order's id is the one
thing the event does carry, and the BFF is the only holder that knows it
deleted that order. Recording it asks nothing of Privacy and adds no state to
the audit row, which holds only a hash and cannot be turned back into an
order. Checking after the write, as ADR-097 does, makes the order of the
erasure and the event irrelevant: an event that commits first is deleted by the
erasure if the row has an owner by then, and one that commits later finds the
id.
**Consequences.** An order id joined to Ordering's row names its buyer until
Ordering anonymises it, so the table holds an identifier for as long as that
join can be made, and `docs/personal-data.md` lists it beside `bff.Orders`. The
id is kept for ever and the table grows by one row per erased order, a cost
paid in exchange for needing no window nobody can bound. It closes the gap only
for an order the BFF deleted: a row that had no owner when the erasure ran, and
whose placing event never arrives again, is as ADR-097 left it, because
nothing says whose it was. An order placed after its customer's erasure is
dropped by ADR-097's check and so recorded here, and its shipment events are
dropped with it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
