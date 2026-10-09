# ADR-093 — The journey test walks one order across the services and asserts that they converge

**Decision.** `Platform.IntegrationTests` holds a second level beside
[§12.6](../12-test-strategy.md)'s contract suite: the journey. It walks one
order through Catalog, Ordering, Inventory, Payments, Shipping and
Notifications, each the real host under the broker account its definitions
give it ([ADR-036](ADR-036-the-broker-has-a-per-service-identity.md)), over one
SQL Server, one broker and one Redis pair, with nothing widened for the
harness. Its edges are the simulators Compose mounts, a stub of Keycloak's
contact read and a relay; Shipping reads the address from Ordering's own gRPC
service. It asserts that the services converge, not that they coordinate. Every
wait is a predicate polled to a deadline, never a sleep, and the deadline is a
sum of legs, each leg [§13.7](../13-observability.md)'s broker-lane and
event-arrival targets and the first rung of the retry ladder, read from the
chapter and from the code and restated nowhere. The states an order passes
through are asserted legal, by what the diagrams can reach, and not only the
one it ends in. One test drives each transition of the order saga that a
service can cause, and the machine's own transitions are enumerated against
them: a transition added to the saga fails the suite until a journey drives
it or it is named with the in-memory suite that holds it. A wait of minutes or
days is delivered as its expiry from Ordering's own bus, and a state that lasts
a message is held by stopping the outbox of the service whose answer ends it.
The fulfilment path runs again under a second deployment's answers
([ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)) that
share no value with the first. This amends the paragraph of
[§12.1](../12-test-strategy.md) that says there is no level of all the
services in containers; the level of a client the backend does not own, which
that paragraph also declines, stays declined.
**Why.** §12.1 declined the level because it fails in ways nobody can
attribute, and put saga coordination in the in-memory harness and contract
compatibility in the shape suite. Both still hold. Neither says whether the
services agree: that the grant one account holds lets through the message its
neighbour sends, that an address Ordering holds reaches the carrier whole, that
the amount one service writes is the amount another renders. Closing the issue
that asked for this on the ground that each service covers its own fan-out left
that to chance, and the first walk found a defect in it: `OrderConfirmed` and
`PaymentRefunded` carried a column's four decimal places for a currency of
two, so a customer was told "39,8000 KZT" in one message and "39,80 KZT" in
the one before it. Notifications renders the wire's decimal at its own scale
([ADR-067](ADR-067-a-currencys-minor-unit-is-iso-4217s-held-once.md)), so the
producer owns the exponent, and an amount read back from `decimal(19,4)` had
lost it. No suite of one service could see that, because the two halves of it
are in two services. The objection to the level is answered by how a failure
reads: a predicate names what it waited for and the deadline, and a legal-state
assertion names the sampled state it did not expect.
**Consequences.** The suite is as slow as the platform's own cadence: a
deployment's world takes about a minute to start and a scenario takes as long
as the slowest poll on its path, so it runs in the integration stage's shard
that no service name selects, and its runtime there is stated by the change
that adds a scenario. The hosts run their own timers, so a scenario passes
only if those timers do, which is the point and also why a slow runner shows
here first. A held outbox and a delivered expiry are faults and clocks the
harness makes, and neither proves that the broker's delayed exchange delivers
([ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md) does that in
its own suite). Scale, crash durability and a dependency's outage stay with the
suites that stage them, and a green journey is cited for none of the three. The
transitions that depend on the broker ordering two services' messages are not
driven here, and the table that says so names the suite that holds each.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
