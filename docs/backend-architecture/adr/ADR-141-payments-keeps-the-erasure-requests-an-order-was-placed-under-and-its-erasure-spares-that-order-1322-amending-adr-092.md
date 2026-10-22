# ADR-141 — Payments keeps the erasure requests an order was placed under, and its erasure spares that order (#1322), amending ADR-092

**Decision.** Payments stores the `ErasedUnder` that an order's `OrderPlaced`
names ([ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)) on `payments.PaymentOrders`, as a JSON column that
defaults to an empty list. Its `OrderPlaced` handler records the order, then,
after that unit commits, asks Payments' own `PersonalDataErasures` less the
rows of those requests, and anonymises that one order's payer when the
customer is erased ([ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)). Payments' erasure anonymises the
subject's orders and empties their lists, but leaves an order whose list holds
the request being run, with its payer and its list, on the request's first
pass and on any reissue of it.
**Why.** [ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md) decides Payments' step as anonymising
`PaymentOrders.CustomerId`, with no exception, and ADR-099 makes an order placed
under a request never that request's to erase, but names only the BFF and
Notifications as the holders that ask. Payments had neither end: a placement
delivered after the customer's erasure wrote the real payer back after the
erasure was reported complete, and a reissued request wiped the payer of an
order placed under it, while Ordering left that order whole. Shipping took the
same exception through [ADR-140](ADR-140-shippings-erasure-keeps-an-order-placed-under-its-request-1275-amending-adr-052.md).
**Consequences.** The column names the buyer for as long as a request that
names them is open ([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)), so the erasure empties it with the payer
rather than keeping it beside the empty id. The erasure's update holds a range
lock to the commit that writes its audit row, even where the subject has no
order, so a placement meanwhile waits and its check then finds that row. An
order placed under the request keeps its payer past that request's erasure, and
goes to a later request. An order placed by an event without the field, from
a publisher older than it, holds the empty list and is erased as before. The
check asks the rows [ADR-139](ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md) leaves it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
