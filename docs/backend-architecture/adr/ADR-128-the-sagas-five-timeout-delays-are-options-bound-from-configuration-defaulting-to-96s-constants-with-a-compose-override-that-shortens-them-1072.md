# ADR-128 — The saga's five timeout delays are options bound from configuration, defaulting to §9.6's constants, with a Compose override that shortens them (#1072)

**Decision.** [§9.6](../09-messaging.md)'s five delays
(`OrderFulfilmentSaga.StockTimeoutDelay`, `PaymentTimeoutDelay`,
`ConfirmationTimeoutDelay`, `ReleaseTimeoutDelay` and `DespatchTimeoutDelay`)
stay the constants they are and become the **defaults** of
`OrderFulfilmentTimeouts`, which Ordering binds from the `Saga` section and
validates at start ([§15.4](../15-cicd-deployment.md)): each between a second
and ten years, a value outside refusing the host. The saga reads them once, in
its constructor, so a change takes a restart. No deployed value sets any of
them; the only setter is `deploy/compose/docker-compose.short-timeouts.yml`, an
override a developer adds to the full-stack `up` in `deploy/compose/README.md`
(never to the infra-only one, whose Ordering runs on the host), which cuts the five waits to seconds so each expiry branch is reached by hand
([§14.1](../14-local-development.md)).

**Why.** Five compensation branches (`StockReservationExpired`,
`PaymentAuthorisationExpired`, `ConfirmationExpired`, `DespatchExpired` and
`StockReleaseExpired`) were reachable only through the journey test's
`ExpireAsync`, which publishes the expiry message itself. A person at the
keyboard has neither that hook nor a wait of seconds, and blueprint-admin's
console cannot script a branch that has no by-hand cause. The two levers
considered were options-bound delays and an operator call that publishes the
expiry for one order. **The call is the one refused**: it is a privileged write
that cancels an order on demand, so it needs its own permission beside
`inventory:admin`, a refusal for every saga state its timeout does not belong
to, and an audit trail, all to serve a developer's stack. A configured delay
widens nothing at runtime, and the constants other chapters cite by name
([ADR-054](ADR-054-a-shipment-stops-waiting-on-its-carrier-at-an-age.md),
[ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md))
keep their one definition.

**Consequences.** A configured delay is one nobody argued: Shipping's
`GiveUpAge` is sized to the default `DespatchTimeoutDelay` and does not follow
a shortened one, and a `PaymentTimeoutDelay` under Payments' redelivery ladder
fires before the ladder ends, which `PaymentWaitTests` asserts only of the
defaults. That is the lever's purpose locally and a defect anywhere else, so
nothing in `deploy/helm` sets the section and the override's header says so.
An order already waiting keeps the delay it was armed with, because the
scheduler holds the message, not the saga; a stack restarted with new values
shows them on the next order only. Two replicas given different values would
arm different waits for one order, so the section is per release and never per
pod. The validator is a copy of Shipping's, because the class library carries
no DataAnnotations package; the second copy is the cost of not adding one to
the pins.
**Superseded in part by
[ADR-130](ADR-130-the-options-validator-is-held-once-in-commoninfrastructure-1075-superseding-adr-128s-sentence-that-the-ordering-validator-is-a-copy-of-shippings.md):**
the last sentence of the Consequences; the validator is held once in
`Common.Infrastructure`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
