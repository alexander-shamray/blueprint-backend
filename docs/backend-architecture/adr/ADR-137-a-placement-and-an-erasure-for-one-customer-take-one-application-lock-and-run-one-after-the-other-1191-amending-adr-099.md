# ADR-137 — A placement and an erasure for one customer take one application lock and run one after the other (#1191), amending ADR-099

**Decision.** Ordering's placement and its erasure each take an exclusive
SQL application lock named for the customer, owned by the unit's
transaction, before either reads or writes anything the other touches: the
placement before it reads its `PersonalDataErasures` rows, the erasure before
its two statements. `IPersonalDataErasureRepository.LockSubjectAsync` takes it,
waiting up to ten seconds, and a lock not granted throws rather than letting
the unit run without it, answered as [§10.5](../10-api-gateway.md)'s lock
refusal. The order summary projection reads the order's customer by the
order's key alone, with `REPEATABLEREAD`, so it holds the order row until its
insert commits.
**Why.** [ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)
reads the erasures in the unit that inserts the order, and the read and the
insert are two statements. With nothing held between them, a placement could
read no audit row, an erasure anonymise the customer's orders and commit,
and the placement then insert an order stamped under nothing that the
erasure never saw, so it kept the customer's id and address after Ordering
had reported the request done, and nothing erased it once the request
closed. A key-range lock, as the BFF's save takes
([ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md)),
serves only where both sides lock the same index. The customer is a
secondary key on `ordering.Orders`, and an update planned as a clustered scan
locks the clustered ranges while the placement locks the customer index's,
so the two need not meet. A named lock depends on no plan, and Privacy
already serialises its own erasure work with one
([E.1](../appendix-e-reference-domain.md#e1-2-and-3-the-service-roster-and-the-context-map)),
whose refusal [E.6.2](../appendix-e-reference-domain.md#e62-error-catalogue-and-refusals)
names beside Ordering's. The projection had the same gap at a smaller scale:
it read the order, an erasure could then anonymise the order and find no
summary, and the projection inserted the buyer's id on a summary nothing
would clear.
**Consequences.** A placement waits behind an erasure of the same customer,
and one customer's placements wait for each other, each for the length of a
unit; no other customer waits. An erasure waits behind a placement in
flight, and behind the projection's write of a summary for an order it is
about to anonymise. A placement still waiting after ten seconds is refused
with a 503 the client retries, and an erasure is retried as its consumer
retries any fault; the projection's wait has no bound of its own but the
command timeout. ADR-099's sentence on an order placed in the instant
between a request's commit and the insert's read now holds: the order is
stamped when the erasure committed before the placement took the lock, and
is erased by it otherwise.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
