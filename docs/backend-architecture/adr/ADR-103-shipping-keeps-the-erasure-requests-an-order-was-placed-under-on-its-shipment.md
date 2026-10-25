# ADR-103 — Shipping keeps the erasure requests an order was placed under on its shipment

**Decision.** Shipping creates its shipment with the `ErasedUnder` that the
order's `OrderConfirmed` names
([ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)),
as a JSON column on `shipping.Shipments` that defaults to an empty list. The
fulfilment claim returns the list with the row, and the worker passes it to its
`IErasedSubjects`, which leaves out the audit rows of those requests: a
shipment is ended by an erasure raised after its order was placed, and by none
the order was placed under. A shipment created before the column existed, or
from an event without the field, holds an empty list and is asked as
[ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)
asks it. Shipping's own erasure empties the list of each shipment whose address
row it deletes, in the same statement batch and unit.
**Why.** ADR-099 left Shipping out because the customer reaches it only through
the address read, so the list had nowhere to live and the worker deleted the
shipment of an order a customer placed after erasing themselves, although it
was paid for. The shipment is the row the claim already leases, so the list
travels with the work and the worker asks the question the BFF and
Notifications ask, of the same order. Storing it on the shipment and not on the
address row keeps it with the thing it describes: the address row is deleted by
retention and by erasure while the shipment stays, and the claim reads the
shipment before any address exists.
**Consequences.** The column is personal data for as long as a request that
names the buyer is open
([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)),
and the shipment outlives the address, so the erasure empties it rather than
deleting the row. A shipment with no address row at that moment, because
the worker has not read one or retention has deleted it, is not reached by that
statement, and no window reaches the column, so it keeps its list; the list
names the buyer only while a request is open. The claim returns one more column
on every leased row, and the migration is additive, so the consumer ships before
the producer as [§9.2](../09-messaging.md) requires, or an order placed after an erasure is still
ended until it does. A shipment already ended before this decision stays ended:
nothing carries the order back.

**Amended by
[ADR-146](ADR-146-shippings-erasure-empties-a-shipments-request-ids-before-it-deletes-the-address-in-two-statements-of-one-unit-1320-amending-adr-103.md)**,
under which the emptying and the address delete are two statements of one
unit, the emptying first, and not one batch.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
