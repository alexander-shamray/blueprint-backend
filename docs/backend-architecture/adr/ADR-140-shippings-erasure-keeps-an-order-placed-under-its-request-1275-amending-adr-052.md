# ADR-140 — Shipping's erasure keeps an order placed under its request (#1275), amending ADR-052

**Decision.** Shipping's erasure hands its request to the three statements
over the subject's addresses: the read of the orders whose waiting shipments
it ends, the emptying of `Shipments.ErasedUnder`, and the delete of
`DeliveryAddresses`. Each leaves an order whose shipment's `ErasedUnder` lists
the request, so that order's address row, its list and its waiting shipment
stay whole, on the request's first pass and on any reissue of it.
**Why.** [ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md) has the erasure consumer delete the subject's
contact rows and end the subject's waiting work, with no exception, and
[ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md) makes an order placed under a request never that request's
to erase. [ADR-103](ADR-103-shipping-keeps-the-erasure-requests-an-order-was-placed-under-on-its-shipment.md) gave the shipment that list, but only the worker
asked it. Keyed by the subject alone, a request reissued under the same id
([ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)) ended a paid, still-pending shipment the subject had placed
after the first pass, emptied its list and deleted its address, while Ordering,
Payments, the BFF and Notifications left the order whole. The shipment carries
the list from its creation, so Shipping needs no new column and no read on the
write side, unlike [ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md) and [ADR-138](ADR-138-notifications-erasure-keeps-what-was-written-under-its-request-1192-amending-adr-117-and-adr-120.md).
**Consequences.** An address saved for an order placed under the request
outlives that request's erasure, and goes by retention or by a later request,
the side every other holder takes. A shipment created before ADR-103's column,
or from an event without the field, holds an empty list and is erased as
before. Each statement joins the subject's address rows to their shipments'
lists, a JSON scan per row on the subject's own request. A late copy of a
request already closed is refused before any statement runs
([ADR-139](ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md)), so the reissue of an open request is the case this
decision changes.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
