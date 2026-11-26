# ADR-217 — The outbox fail statement matches only the lease its pass holds (#2186)

**Decision.** The outbox claim returns the `LockedUntil` it set, as
`OutboxClaim.Lease`, and `OutboxDispatcher`'s fail statement carries
`AND LockedUntil = @Lease`, so a pass whose lease lapsed and was claimed again
by another replica writes neither the row's lease nor its `LastError`. The
complete statement keeps matching on `Id` alone
([§9.4](../09-messaging.md)).
**Why.** Nothing bounds a delivery below `OutboxDispatcher.LeaseSeconds`. A
pass that outlived its lease and then failed pushed the second replica's lease
back to its own back-off, or cleared it at the cap, so a third replica could
claim the row while the second still delivered it, and at the cap §13.6's
abandoned count read a row still being tried as abandoned; it also replaced
the second replica's error with a stale one (#2186). Every sibling claim
worker, Shipping's and Notifications', already guards its back-off this way.
Completion is left unguarded because a lapsed pass completes only rows it
delivered: guarding it would leave them for a further delivery rather than
end the duplicate.
**Consequences.** A lapsed pass whose row was claimed again no longer records
its failure, so its error is lost to `LastError` and survives only in the log
the dispatcher writes before the statement. A lapsed pass still completes rows
the second replica is delivering, so the lease still bounds only crash
recovery, and a batch slower than the lease remains a duplicate delivery the
inbox absorbs.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
