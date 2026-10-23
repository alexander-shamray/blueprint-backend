# ADR-146 — Shipping's erasure empties a shipment's request ids before it deletes the address, in two statements of one unit (#1320), amending ADR-103

**Decision.** Shipping's own erasure sends two statements on the unit's
transaction, not one batch: the first empties the `ErasedUnder` of each
shipment whose address row names the subject, the second deletes the
subject's address rows. The emptying runs first, while the address row still
names the order's customer, and both statements leave an order placed under
the request being run, as [ADR-140](ADR-140-shippings-erasure-keeps-an-order-placed-under-its-request-1275-amending-adr-052.md)
decides. The statements stay separate: the unit is what makes them one erasure,
and an address row saved between them is deleted by the second while its
shipment keeps its list, the case ADR-103 already allows for a shipment with no
address row when the emptying runs.
**Why.** [ADR-103](ADR-103-shipping-keeps-the-erasure-requests-an-order-was-placed-under-on-its-shipment.md)
says the emptying runs in the same statement batch and unit as the address
delete. The code sends two commands, so it does not honour the word batch, nor
each shipment whose address row it deletes where a row is saved between them:
the emptying reaches the rows present when it runs. What the rule rests on
holds: both commit or neither does, and the emptying cannot follow the delete,
because once the address row is gone nothing joins the shipment to the subject.
Merging the two into one batch would make the word true and change nothing
the rule depends on, so the code stays as it is and the sentence is amended.
**Consequences.** A reader of ADR-103 finds the word batch there, as an ADR is
amended and never rewritten, and reads this decision beside it. A change that
reorders the two statements, or runs the delete in a unit that does not also
hold the emptying, breaks the rule, where one that merges them does not.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
