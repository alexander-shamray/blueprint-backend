# ADR-191 — A consumer takes a delivery only through its type's exchange or its endpoint's own (#1684), amending ADR-036

**Decision.** Every receive endpoint runs `SourceExchangeFilter<T>` outermost
on each consumer's and each saga's message pipe, and it passes a delivery on
only when the exchange RabbitMQ names in `basic.deliver` is the consumed
type's publish exchange, the exchange named for the endpoint's own queue,
or the delayed-message exchange in front of that one, which carries
`SourceExchange.DelaySuffix`; `SourceExchange.Admits` is the rule and
`UseSourceExchangeCheck` the call.
`UseInboxAndInMemoryOutbox` adds it in front of the inbox, the saga's
endpoint, which takes the inbox alone, adds it itself, and
`InboxEndpointExtensionsTests` holds every endpoint that takes the inbox
alone to a source check beside it. A refused delivery is not consumed and
not thrown: with no other consumer taking it, MassTransit parks it in
`<queue>_skipped`, which [§13.6](../13-observability.md) pages on, and the
filter logs its type, exchange, endpoint and `MessageId` at warning.
**Why.** MassTransit picks the consumer from the message type the envelope
declares, and the envelope is the publisher's to write.
[ADR-036](ADR-036-the-broker-has-a-per-service-identity.md) scopes each
account's `write` to its own exchanges and measured a forged `OrderPlaced`
and `PaymentAuthorised` refused, but only as publishes to the forged type's
own exchange. Several queues bind exchanges of more than one publisher: the
saga's binds Inventory's `StockReleased` beside Payments' `PaymentAuthorised`,
so `inventory-svc` could publish to its own exchange an envelope naming
`PaymentAuthorised` and an order in `AwaitingPayment` was confirmed with no
payment; `inventory-events`, `bff-order-events` and `notifications-events`
mixed publishers the same way. The exchange in `basic.deliver` is set by the
broker from the publish, so a publisher can choose only an exchange its grant
lets it write, and the rule turns ADR-036's grant on exchanges into a grant on
the types each consumer takes. A published event names the type's exchange,
a `queue:` or `exchange:` send to the endpoint and the dead-letter replay
name the endpoint's, which `SourceExchangeFilterBrokerTests` measures on a
broker holding the deployed grants, and a saga timeout and a delayed
redelivery go through
[ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)'s plugin and
name the delayed exchange: that stock image has no plugin, so Ordering's
`ScheduledStockTimeoutTests` and Payments' `PaymentsCommandEndpointTests`
hold it, and both fail with the delay suffix refused. Only the queue's owner
may write its delayed exchange, since a peer's command grant is anchored at
the queue's name. Giving each publisher's events a queue of
their own was the alternative and was refused: it adds an endpoint per
publisher to every consumer and holds only until a binding mixes them again,
where the filter holds at every endpoint the helper builds.
**Consequences.** The endpoint's own exchange admits any type the endpoint
consumes, so whoever may write it — the owner, the senders its peer
command grants name, and
[ADR-072](ADR-072-a-person-replays-dead-letters-as-a-broker-account-of-its-own.md)'s
`dead-letter-operator`, whose `write` covers every endpoint's exchange — can
still deliver any of those types there; that is the command boundary ADR-036
already draws, and each holder writing
`privacy-completions` keeps
[ADR-190](ADR-190-holders-send-to-privacys-exchange-and-fault-reads-stop-at-consumed-namespaces-1682-1683-amending-adr-036-and-adr-094.md)'s
residual unchanged. A message published to the default exchange, as the
management UI's *Publish message* on a queue does, is refused and parks in
`<queue>_skipped`; the runbooks' replay already publishes to the endpoint's
exchange. On the in-memory test transport, whose deliveries name no exchange,
the filter passes everything, and any other transport is refused, so a
change of transport fails closed. A consumer of a base type or interface
would be refused the concrete type's publishes; every consumer takes a
concrete contract today, and one that does not owes this rule a case.
`Common.Infrastructure` now references the RabbitMQ transport package, for
`RabbitMqBasicConsumeContext`. ADR-036 is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
