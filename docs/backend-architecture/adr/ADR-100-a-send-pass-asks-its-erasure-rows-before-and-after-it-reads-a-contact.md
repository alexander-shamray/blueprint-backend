# ADR-100 — A send pass asks its erasure rows before and after it reads a contact

**Decision.** Notifications' send pass asks its own `IErasedSubjects`
([ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md))
twice on the path that reads a customer's contact from the owner: before the
owner is called, and again once the answer has been saved. A match before the
call deletes the stored contact and answers no such customer, so the owner is
never asked; a match after the save deletes what was just saved and answers no
such customer, so nothing is sent. The notice ends undeliverable by the rule
that already ends one for a customer the owner does not know
([ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
A stale row served because the owner could not answer is asked about once more
before it is returned, as the call may have run through its retries. A contact
fresh enough to be served without the owner is not asked about: an erasure
deletes it, and a save that raced the erasure is caught by the second question.
**Supersedes.** The part of ADR-097's Consequences that leaves a worker's one
fetch of the mailbox, between the intake's commit and its delete, unclosed. The
fetch no longer happens once the erasure's rows are visible, and the mailbox a
fetch returns is neither kept nor sent.
**Why.** The intake's check runs after its unit commits, so for the length of
one query the notice and the order record exist for an erased customer. A send
worker that claims the notice inside that interval resolves the customer
through the order record, reads the mailbox from the owner and saves it, and
the erasure's delete may already have run. Asking after the save is
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s
form: an erasure that commits at any moment either finds the row or is seen by
the check. Asking before the call is what keeps the mailbox from being fetched
at all in the common case of a notice claimed after the erasure.
**Consequences.** Each pass that reads a contact from the owner pays the scan of
the audit table twice, once per customer through the pass's shared read; the
audit row keeps no customer id, so neither is a seek. A fetch that began before
the erasure still reaches the owner once, as the question before it was true:
only the second question stops it from being kept or sent. An order a customer
places after their own erasure is treated as ADR-097 treats it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
