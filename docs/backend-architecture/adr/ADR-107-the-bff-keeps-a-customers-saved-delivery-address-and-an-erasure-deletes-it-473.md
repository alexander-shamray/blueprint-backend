# ADR-107 — The BFF keeps a customer's saved delivery address, and an erasure deletes it (#473)

**Decision.** `Web.Bff` owns a customer's saved delivery address, in
`bff.SavedAddresses`: one row, keyed by the customer's id, holding the five
fields an order's shipping address has. Three routes under
`/v1/saved-address` read, replace and delete the caller's own row, with the
subject taken from the principal and never from the request
([§10.9](../10-api-gateway.md)). The address is validated by presence and the
shape of an ISO 3166-1 alpha-2 code, and by the widths the order's own
validator admits, so an address that saves is one an order accepts; no
postcode format is checked. The saved address is a prefill and nothing reads
it but its owner: placing an order still sends the address, and the order's
contract is unchanged. `PersonalDataDeleteRequested`'s handler in the BFF
deletes the row in the transaction that deletes the subject's orders, and
counts it in the audit row. The row has no retention window: it lasts until
its customer replaces or deletes it, or an erasure does. The address is never
a log attribute and never in an exception's text. It is not Shipping's
`DeliveryAddresses`, which copies an order's address from its owner
([ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
**Why.** Three owners were open. The service that owns orders holds an address
as a fact about one order, which is the financial record and is anonymised
rather than deleted; a preference the customer edits and can lose on demand is
a different lifetime, and putting it there gives the order aggregate's service
a second reason to change and a table that must be deleted beside rows it
must keep. A Keycloak user attribute travels with the user representation,
which a service account with a wide grant reads
([ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
so the address would reach every reader of the contact for no reason of
theirs. The BFF has a schema of its own since
[ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md), is
already in
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s
responder set with a delete that runs, and serves the screen that wants the
prefill, so it adds a table and no participant: the set Privacy fixes when a
request is raised does not change. Leaving the order request alone keeps a
stale saved address from silently redirecting an order, because the client
shows the prefill and the customer confirms it.
**Consequences.** The platform holds an address in one more place, and
`docs/personal-data.md` lists it. With no window, a customer who never returns
leaves their address until an erasure reaches it; a retention bound is the
adopter's to add, as it is for every other holding whose window the contract
leaves open. The widths are copied from the order's validator because
[§4.2](../04-solution-structure.md) lets the BFF reach no service type, so a
service narrowing its own would leave a saved address its order refuses, which
the client sees as the order's 400 and not as a defect here. The row is not
written from a feed, so [ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)'s
check after a write does not apply: a customer who saves an address after
their erasure, on a token still valid, makes a new row of their own act, and
a later erasure request removes it.

**Amended by [ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md).**
The handler deletes the row only when it was not saved under the request
being handled, so "a later erasure request" holds for a reissue and a late
copy of the same request as well, which the delete by the customer's id
alone did not keep.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
