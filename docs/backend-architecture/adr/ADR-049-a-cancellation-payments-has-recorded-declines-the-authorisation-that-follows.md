# ADR-049 — A cancellation Payments has recorded declines the authorisation that follows

**Decision.** Payments records `OrderCancelled` on its own record of the order,
creating the record as a tombstone when `OrderPlaced` has not arrived. An
`AuthorisePayment` for an order so marked calls no provider and publishes
`PaymentDeclined` with reason `order_cancelled`. `PaymentRefunded` is published
only when money moved back, so a cancellation of an order with nothing
authorised publishes nothing.

**Why.** [§9.4](../09-messaging.md) orders nothing between two deliveries, so
the saga's `AuthorisePayment` and Ordering's `OrderCancelled` can reach
Payments in either order — the race
[ADR-024](ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)
closed for Inventory, one service over. Without a guard the late command
charges a cancelled order, and it charges it while the saga is in
`Compensating`, where [§9.6](../09-messaging.md) answers a `PaymentAuthorised`
by escalating to a person: a customer charged for an order they cancelled, and
a human paged to undo it. Payments already keeps a record of the order to
resolve the payer from
([ADR-028](ADR-028-a-money-movement-command-carries-no-subject.md)), so the
cancellation has somewhere to land that the command reads anyway. A decline
settles the saga's payment half at once; staying silent would hold it until
§9.6's payment timeout for a verdict that is already known.

**Why a decline and not a postcondition event.** ADR-024 answers a refused
reserve with `StockReleased` because that event states a postcondition — no
stock is held for the order — which is true whether or not anything was ever
reserved. Payments' three events state acts, not postconditions, and none of
them says "nothing is charged". `PaymentDeclined` is the one whose meaning
survives: the order will not be paid for. Its `Reason` is for a human and
never branched on ([§9.8](../09-messaging.md)), so a reason Payments owns
beside the provider's codes is the cheapest honest answer, and it moves no
contract and no consumer. A new event would have been a second verdict the
saga must learn to read for the same outcome.

**Why the refund is not symmetric.** ADR-024 answers every release, because
the saga waits on `StockReleased`. Nobody waits on `PaymentRefunded` but
Notifications, and what it tells is a customer: a refund event for money never
taken announces a refund that did not happen. So the event reports the act,
and where there was no act there is no event.

**Consequences.** The authorisation and the cancellation for one order are
serialised by the lock on Payments' record, and the provider call happens
inside that lock, so a slow provider holds the row and the cancellation waits
behind it rather than landing between the check and the charge. A cancellation
that arrives for an order Payments never hears placed leaves a tombstone that
nothing reaps — the same cost ADR-024's tombstone carries, and for the same
reason: the row is an answer a later message may still ask for. And
`PaymentDeclined.Reason` now has two authors, the provider and Payments;
anything that reads it as the provider's word alone reads it wrongly, which is
tolerable only because nothing may branch on it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
