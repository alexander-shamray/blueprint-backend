# ADR-100 — A send pass asks its erasure rows after it saves a contact

**Decision.** Notifications' send pass asks its own `IErasedSubjects`
([ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md))
once the owner's answer has been saved to the contact table, and deletes what it
saved when a request names the customer. It passes no requests, so the question
is ADR-097's, unbounded by the order. The pass still sends the notice it was
working on: the answer it holds is returned unchanged.
**Why.** The intake's check runs after its unit commits, so for the length of
one query the notice and the order record exist for an erased customer. A send
worker that claims the notice inside that interval resolves the customer
through the order record, reads the mailbox from the owner and saves it, and
the erasure's delete may already have run. That leaves a mailbox in the table
that nothing will delete. Asking after the save is
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s
form: an erasure that commits at any moment either finds the row or is seen by
the check. Asking before the owner call, or declining to send, was rejected.
[ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)
keeps an order placed after an earlier erasure, and only the order's own
`ErasedUnder` tells that order from one the erasure was for. Notifications does
not keep it on its order record, so a worker asking about the customer cannot
tell them apart, and would end the notice of an order that decision kept.
**Consequences.** A pass that reads a contact from the owner pays the scan of
the audit table once more, and the audit row keeps no customer id, so it is not
a seek. For a customer whose erasure ADR-099 does not count against an order,
the row never holds: each read, and each pass repeated after a crash, calls the
owner, where [ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
would have served the stored one. That is the whole cost of asking without the
bound. The fetch stays open, as ADR-097 says, and so does the one send it
leads to: closing them needs the order's `ErasedUnder` stored on Notifications'
order record, which is its own decision. A stale row served for an owner that could not answer writes nothing
and is not asked about.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
