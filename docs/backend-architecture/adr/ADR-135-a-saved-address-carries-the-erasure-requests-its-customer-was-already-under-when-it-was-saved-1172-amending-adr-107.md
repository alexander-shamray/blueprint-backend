# ADR-135 — A saved address carries the erasure requests its customer was already under when it was saved (#1172), amending ADR-107

**Decision.** `bff.SavedAddresses` keeps the ids of the erasure requests its
customer was already under as `ErasedUnder`, a JSON list beside the address.
A save reads the BFF's own `PersonalDataErasures` rows for the customer, by
trying every row's hash as the projection's check does, in the transaction
that writes the row, and stores the ids it finds. The save first takes the
customer's key with `UPDLOCK, HOLDLOCK`, held to its commit whether or not a
row is there, and the erasure handler's delete takes `HOLDLOCK`, so a save
and an erasure for one customer run one after the other. The handler deletes
the address only when its `ErasedUnder` does not list the request being
handled: an address is erased by a request that reached the BFF after it was
saved, and by none it was saved under.
**Why.** [ADR-107](ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md)
promises that an address a customer saves after their erasure is removed by
a later request, and the handler deleted it by `CustomerId` alone, so a
request reissued under the same id
([ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)),
or a copy delivered late, removed it as well. A cut-off on the audit row's
time is unsound, because a repeat moves that time to its own pass, so a
third pass would delete what was saved between the first two. The BFF can
stamp the address as Ordering stamps an order
([ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)),
because it both erases the address and takes the next save, in one database,
so what the save finds there is the erasures that had committed before it.
The locks are what make "before" mean the same thing to both here: without
them a save could read no audit row, an erasure commit in between, and the
save then insert an address that the first pass never saw and a reissue would
delete.
**Consequences.** Each save scans an audit table with no window
([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)),
the same read the projection makes per event, so its cost falls on the
customer's save and grows with every erasure the BFF has ever applied. An
address saved after a request was raised but before the BFF consumed it is
saved under nothing and is deleted by that request, which is the side that
fails safe. The list joined to a request still open names its customer, which
the row already does by its key, so it adds no holding to
[`docs/personal-data.md`](../../personal-data.md), only a column to its row.
An address saved before this column existed carries the empty list and is
deleted by any request, as it was before.

**Amended by [ADR-139](ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md).**
The save's read asks the rows still open and those closed within a margin of
the save, and the handler refuses a copy of a request whose row it already
holds closed, so an address saved after the BFF holds a request closed is not
that request's to delete.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
