# ADR-145 — A receive endpoint off at start has its topology declared by a bus that only deploys it (#1360), amending ADR-144 and ADR-026

**Decision.** When a bus that the operator switch governs starts, each endpoint
that
[ADR-144](ADR-144-a-receive-endpoint-switched-off-when-its-bus-is-configured-is-not-configured-until-its-switch-is-on-1289-amending-adr-113.md)
left unconfigured has its queue, exchanges and bindings declared by a second,
short-lived bus. That bus is configured with MassTransit's
`DeployTopologyOnly` and the endpoint's own configuration, started and
stopped. `ConnectOperatorSwitch` takes the factory for that bus from the host,
which builds it on the same broker configuration as its own bus; each switched
host has one `UseBroker` method that both buses call. A declaration is bounded
by `ReceiveEndpointSwitch.TopologyTimeout` and is retried with the switch's
backoff until it lands. `ReceiveEndpointSwitch.TopologyPending` names the
endpoints still waiting for one.
**Why.** An endpoint that is never configured declares nothing, so under
ADR-144 a queue the broker had never seen, or a binding a release added to an
existing queue, did not exist while the endpoint was off. A message routed to
it reached no queue and was lost, and it reached no `_skipped` queue either, so
no alert fired (#1360). RabbitMQ's transport under `DeployTopologyOnly` builds
the endpoint's channel pipe from the topology filter and a ready filter alone,
with no prefetch and no consumer filter. It therefore declares what the
endpoint would and never issues `basic.consume`, and both the setting and
`DeployAsync` are public API. A broker test publishes through a binding that
exists only in the deferred endpoint's configuration. It shows the message
waiting in the queue while the switch is off and consumed once it is on; with
the declaration removed, the same test fails, because the message is lost.
**Consequences.** Each pod start opens one more broker connection per endpoint
that is off at start, for as long as its declaration takes. The topology bus
must be configured as the host's bus is: a bus-level topology convention, such
as an entity name formatter, added to one bus and not the other would declare
bindings the host never uses. Sharing `UseBroker` covers the broker; any
bus-level convention a host adds goes into that method as well. Until a
declaration lands, for example while the broker is unreachable as the pod
starts, ADR-144's window stays open for that endpoint. Each failed attempt is
logged as an error, and `TopologyPending` names the endpoint. Readiness does not
say the declaration is pending: the `operator-switches` check reports the
endpoint only as switched off. The in-memory transport ignores
`DeployTopologyOnly`, so the unit tests stand in a recording factory for the
topology bus, and only the broker test proves that nothing is consumed.
ADR-144's note on
[ADR-026](ADR-026-consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it.md)
no longer holds once a declaration has landed: a consumer released while its
switch is off counts as deployed when its pod has started and declared its
topology. Its messages then wait in its queue rather than routing nowhere.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
