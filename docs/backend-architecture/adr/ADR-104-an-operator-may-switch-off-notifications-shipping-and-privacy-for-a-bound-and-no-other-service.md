# ADR-104 — An operator may switch off Notifications, Shipping and Privacy for a bound, and no other service

**Decision.** A service is classified by what its absence costs, and an
operator switches off only a service the classification permits, for no longer
than the bound it names. The bound is a symbol, never a value.

| Service | May be off | Bound | Degraded while off |
|---|---|---|---|
| Notifications | Yes | Less than `PrivacyOptions.CompletionSlo` | Mail goes out late; each open erasure request waits for its completion |
| Shipping | Yes | The lesser of `OrderFulfilmentSaga.DespatchTimeoutDelay` and `PrivacyOptions.CompletionSlo` | Confirmed orders wait for despatch, and past the first bound the saga raises `FlagOrderForReview` for each; open erasure requests wait for its completion |
| Privacy | Yes | Less than `PrivacyOptions.CompletionSlo` | Open erasure requests wait, each due `CompletionSlo` after it was raised |
| Payments | No | — | `PaymentTimeout` cancels orders awaiting a verdict |
| Inventory | No | — | `StockTimeout` cancels orders awaiting a reservation |
| Ordering | No | — | No order is placed, confirmed or advanced; the saga itself is down |
| Catalog | No | — | The BFF's checkout quote fails, so no customer can check out; Ordering still prices from its projection, which goes stale |

Suspending a service stops its consumers and its workers and leaves its
migrations, its outbox and its broker queues in place, so that nothing it was
sent is lost. A bound counts from the oldest order or request already waiting
when the service stops, not from the stop, so the time left is the bound less
that age. A suspension longer than the time left is an incident to be declared,
not an operation to be extended.
**Why.** [§9.7](../09-messaging.md)'s third rule asks for a fallback decided in
advance, and [§9.8](../09-messaging.md)'s *Consumer down* row says only that
messages wait. Which services may be stopped on purpose, and for how long, was
readable only from the saga's timeouts and each service's code, and an operator
would have worked it out in the incident. The classes are not a preference:
Notifications publishes no event an order waits on and has no caller, so late
mail is the whole cost to an order, and its one other duty is the erasure
completion it owes Privacy, as a responder
([ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)),
which Shipping owes too. Each bound cites the
deadline another component already owns rather than restating it, and the other
four services are the order's own path or the customer's way onto it:
the saga's timeouts turn a stop of Payments or Inventory into cancelled
orders, Ordering is the saga's host, and Catalog's absence ends the BFF's
quote, the one synchronous hop
([§9.7](../09-messaging.md)), although no write waits on it.
**Consequences.** The table is a second statement of what each service's
absence does, and a new service, or a new timeout in the saga, must be placed in
it or the operator is back to reading code. Nothing here makes a suspension
possible: the Helm switch, the runtime kill switch and the gateway's route-level
503 are separate decisions that cite this one, and until they land an operator
stops a service with the tools they already have. Four of seven services may
never be stopped, which means a fault in one of them is repaired in place under
load, not by switching it off while someone thinks.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
