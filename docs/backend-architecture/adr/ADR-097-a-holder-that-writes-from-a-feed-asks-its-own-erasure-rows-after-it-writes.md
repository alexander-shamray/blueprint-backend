# ADR-097 — A holder that writes from a feed asks its own erasure rows after it writes

**Decision.** A holder that creates or refreshes a row for a customer from an
event asks its own `PersonalDataErasures` rows, after the write has committed,
whether that customer is erased, and deletes what it wrote when one is. This
applies to the BFF's order projection, which `tools/bff-replay` rebuilds
through, and to Notifications' intake of Ordering's three order events. The
question is Shipping's `IErasedSubjects`
([ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)):
each row's hash is recomputed for the customer under that row's own request id,
and the read is not bounded by the order's age. Each holder carries its own port
and its own SQL implementation, and none is lifted into `Common`. On a match the
BFF deletes the order row, whose lines follow by the foreign key's cascade;
Notifications deletes the customer's waiting notices, order records and contact,
in the erasure's order, so no notice is left for the worker to send. The events
that carry an order id and no customer write no customer, and are left alone.
**Why.**
[ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)'s rebuild
re-reads the publishers' outbox rows, which still name the customer until
`RetentionPolicy.OutboxWindow` passes, and Notifications is fed the same events
live; either puts back a customer whose data was erased, after the erasure's own
delete has run. ADR-092 closed the same window for Shipping by asking after the
save, so an erasure that commits at any moment either finds the row to delete or
is seen by the check; the holders that write from a feed had no such check.
Three other ways were rejected. Privacy keeping the subject's id for the window
re-holds the raw id that ADR-092 and
[ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)
deliberately drop when a request closes. A runbook ordering a rebuild outside
the window is a rule on paper that nothing enforces, and it does not reach a
live event still in flight. A bound by the order's own time, so that an order
placed after the erasure is kept, is what Shipping tried, reading only the
erasures since the shipment was created, and withdrew: the audit row is written
on the holder's clock and the owner anonymises on its own, so an order created
after the holder erased but before the owner anonymised still carries the id,
and the bound skips the row that should stop it.
**Consequences.** The question is a scan of a table that has no window, paid
once per event that names a customer, and a rebuild pays it for every such
event; the audit row keeps no customer id, so it cannot be an index seek. A
positive answer could be remembered and a negative one cannot, since an erasure
may land during the run, so none is. An order a customer places after their own
erasure is dropped as well: while the owner still names the customer, nothing
the holder can compute tells it from the order the erasure was for, and keeping
it would need Privacy's view of when every holder has answered, which no holder
has. Between the commit and the delete the row exists for the length of one
query, and a Notifications worker that claims the notice in that interval can
fetch the mailbox once, which this decision does not close. Two ports named
`IErasedSubjects` now sit beside Shipping's, which is the price of keeping each
holder's audit table to its own service; a shared form is its own decision.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
