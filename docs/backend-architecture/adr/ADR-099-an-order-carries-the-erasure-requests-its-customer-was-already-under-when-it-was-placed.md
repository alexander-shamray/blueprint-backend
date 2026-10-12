# ADR-099 — An order carries the erasure requests its customer was already under when it was placed

**Decision.** Ordering reads its own `PersonalDataErasures` rows for the
customer in the unit that inserts an order, and the order keeps the id of each
request whose hash names that customer as `Order.ErasedUnder`. The order's
three events, `OrderPlaced`, `OrderConfirmed` and `OrderCancelled`, repeat the
list as an optional `ErasedUnder` on the wire. The BFF's projection and
Notifications' intake pass it to `IsErasedAsync`, which leaves out the rows of
those requests: an order is erased only by a request that was raised after it
was placed, and by none it was placed under. An event without the field, from
a publisher older than it, names no request and is asked as
[ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)
asks it. Shipping is not changed by this decision.
**Why.** ADR-097 left every audit row unbounded by the order's age, because a
holder's row is written on the holder's clock and the owner anonymises on its
own, so no instant a holder can compute separates the order an erasure was for
from one placed after it. Ordering can: it is the service that both erases the
customer's orders and takes the next one, in a database it reads in the same
unit as the insert, so what it finds there is the erasures that had committed
before the order existed. A request it did not find cannot have been about
this order, and a request it found cannot have been about anything placed
after it. The order says so itself, so a holder asks nothing of Privacy, whose
view of when every holder has answered is not a thing a holder has, and keeps
no state of its own. The alternative that looked smaller, each event carrying
the instant of its order's placement against the time on the holder's row,
repeats the bound ADR-097 records Shipping withdrawing. Stamping only
`OrderPlaced` fails the other two events, which each ask the same question of
a customer and would delete the order a placement had kept.
**Consequences.** Each placement scans a table with no window
([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)),
trying every row's hash, as a holder's check does once per event; the price
falls on the customer's checkout and not on a worker. An order placed in the
instant between a request's commit and the insert's read is stamped or not as
the read saw it, and the unstamped order is erased by that request, which is
the side that fails safe. A request id joined to a request that is still open
names the buyer, because Privacy keeps the subject's id until it closes
([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)),
so Ordering's own erasure empties the list with the customer's id and the
three events already in the outbox keep what they were published with.
Three events grow by a field that older consumers ignore, and the consumers
ship before the producer, as [§9.2](../09-messaging.md) requires, or an order
placed after an erasure is dropped until they do. Shipping still deletes the
shipment of an order placed after an erasure: it creates the shipment from
`OrderConfirmed` with no customer and learns the customer from the address
read, so it would need the list stored on the shipment and read with the
claim, which is its own decision. Where a holder already deleted such an order
before this decision, nothing brings it back: the rebuild reads the outbox,
whose rows lack the field.

**Superseded in part by
[ADR-103](ADR-103-shipping-keeps-the-erasure-requests-an-order-was-placed-under-on-its-shipment.md)**:
the Decision's last sentence and the Consequences' sentence on Shipping.
Shipping keeps the shipment of such an order, because the shipment stores the
list and the fulfilment claim returns it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
