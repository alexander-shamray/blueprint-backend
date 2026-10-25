# ADR-101 — An order record keeps the requests its order was placed under, and the send pass asks them before the owner

**Decision.** Notifications' `OrderRecord` keeps the `ErasedUnder` its first
event names ([ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)),
as a JSON column that defaults to an empty list, and the intake writes it with
the record. Before the send pass asks the owner for a customer's mailbox it
passes the order record's list to its own `IErasedSubjects`, and when a request
names the customer it fetches nothing and sends nothing: the row is left to the
intake's check or the erasure, either of which deletes it. A pass that finds no
request goes on as
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
and [ADR-100](ADR-100-a-send-pass-asks-its-erasure-rows-after-it-saves-a-contact.md)
have it, and keeps the check after the save.
**Why.** ADR-100 left the fetch and the one send it leads to open, because the
worker resolves the customer through the order record and the record held
nothing that tells an order an erasure was for from one placed after it. With
the list, the worker asks the question the intake asks, of the same order: the
two cannot disagree, so a notice the intake would delete is not sent, and an
order ADR-099 kept is not ended. An erasure that commits after the worker's
question finds the notice's rows, which the intake's unit committed before the
worker could claim them, and deletes them, so the only send left is one that
precedes the erasure.
**Consequences.** Every pass that reaches the contact read pays the scan of
the audit table once more, before the read and not only on a fetch, and the
audit row keeps no customer id, so it is not a seek. The list is personal data
for as long as a request that names the buyer is open ([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)), so it is
deleted with the record, by the same statements, and `OrderRetention` ages it as
it ages the record. A record written before the column existed holds an empty
list, and its question is ADR-097's, unbounded by the order. A notice left by
the pass waits for the row's delete and not for a backoff, so a delete that
never comes leaves a row the lease returns to the queue every pass.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
