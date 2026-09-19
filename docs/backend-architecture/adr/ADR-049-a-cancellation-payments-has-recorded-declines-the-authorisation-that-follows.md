# ADR-049 — A cancellation Payments has recorded declines the authorisation that follows

**Decision.** Payments records `OrderCancelled` on its own record of the order
([§3.2](../03-bounded-contexts.md)), creating the record as a tombstone when
`OrderPlaced` has not arrived. An `AuthorisePayment` for an order so marked
calls no provider and publishes `PaymentDeclined` with reason
`order_cancelled`. That decline is a verdict on the order, not on a payer, so
it stands for a tombstone as much as for a placed order; where Payments holds
no record of the order at all, the command still waits, as §3.2's callout on
the subscription requires. `PaymentDeclined.Reason` is for a human: nothing
branches on it, and nothing makes it a metric dimension. `PaymentRefunded` is
published only when money moved back, so a cancellation of an order with
nothing authorised publishes nothing.

**Why.** [§9.4](../09-messaging.md) orders nothing between two deliveries, so
Ordering's `OrderCancelled` can reach Payments ahead of the saga's
`AuthorisePayment` — the race
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

**Why a decline for a payer Payments may not know.** ADR-028 and §3.2's
callout refuse `PaymentDeclined` when Payments holds no record, because it is
"a business verdict about a payer it has not identified". This decline judges
no payer: it judges the order, which Ordering has already cancelled, so it is
the case their reason does not reach, and a tombstone that names no payer
serves as well as a placed order that does. The case they govern — no record
of the order at all — is unchanged and still a wait.

**Why a decline and not a postcondition event.** ADR-024 answers a refused
reserve with `StockReleased` because that event states a postcondition — no
stock is held for the order — which is true whether or not anything was ever
reserved. Payments' three events state acts, not postconditions, and none of
them says "nothing is charged". `PaymentDeclined` is the one whose meaning
survives: the order will not be paid for. Because nothing branches on its
`Reason`, a reason Payments owns beside the provider's codes is the cheapest
honest answer, and it moves no contract and no consumer. A new event would
have been a second verdict the saga must learn to read for the same outcome.

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
nothing reaps — the cost
[ADR-048](ADR-048-a-reservation-row-is-kept-for-as-long-as-its-order.md)
accepts for Inventory's rows, and for the same reason: the row is an answer a
later message may still ask for. And `PaymentDeclined.Reason` now has two
authors, the provider and Payments; anything that reads it as the provider's
word alone reads it wrongly, which is tolerable only because this record
forbids branching on it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
