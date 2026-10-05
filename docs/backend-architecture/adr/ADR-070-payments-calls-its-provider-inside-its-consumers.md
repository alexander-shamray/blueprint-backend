# ADR-070 — Payments calls its provider inside its consumers

**Decision.** Payments' consumers that authorise a payment and void or refund
one on a cancellation call `IPaymentProvider` synchronously, inside the
consume. This is the written exception
[ADR-017](ADR-017-one-synchronous-hop.md) asks for. When the provider is
unreachable the message is retried, no verdict is recorded, and
`ProviderKillSwitch` stops the endpoint so the rest of the queue waits rather
than faults: correctness is chosen over availability. When the provider
answers no, the decline is recorded and published like any other verdict.
**Why.** The provider is a third party reached only by a request
([§3.1](../03-bounded-contexts.md)'s anti-corruption layer), and the work that
needs it arrives as a message, so the call has nowhere else to happen. A
worker over a leased row, the shape
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
chose for a read, would add a table and a second retry loop to carry a
command the broker already carries durably. What ADR-017 guards against is a
consumer that turns an outage into lost work; here the idempotency key makes
every retry safe, `ProviderHop` bounds each attempt, and the kill switch keeps
the unconsumed messages on the queue.
**Consequences.** A provider outage stalls Payments' endpoints and with them the
saga's payment wait, which [§9.6](../09-messaging.md) bounds; an order whose
wait expires is compensated as that section says. `ConsumerCallRule` holds this
record to the composition, so a second client a Payments consumer reaches is
refused until a record grants it too, and this record grants nothing to any
other service.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
