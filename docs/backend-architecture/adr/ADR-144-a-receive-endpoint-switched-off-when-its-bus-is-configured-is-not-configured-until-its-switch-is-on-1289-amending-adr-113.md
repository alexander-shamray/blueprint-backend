# ADR-144 — A receive endpoint switched off when its bus is configured is not configured until its switch is on (#1289), amending ADR-113

**Decision.** A host the operator switch governs declares each receive
endpoint through `SwitchedReceiveEndpoint`, never `ReceiveEndpoint`. When the
endpoint's name is in `Switches:Off` as the bus is configured, the endpoint is
not configured at all: `ReceiveEndpointSwitch` keeps its configuration and
counts it stopped, and when the name leaves the list it connects the endpoint
through MassTransit's `IReceiveEndpointConnector`, after which it is stopped
and started like any other, as
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)
decides. An endpoint whose name is off only by the time it is ready is still
stopped from its `Ready`. `ReceiveEndpointSwitch.Unregistered` names any
endpoint a switched bus configured without the helper, and each switched
host's test asserts it is empty.
**Why.** MassTransit's RabbitMQ transport issues `basic.consume` before it
raises `Ready`, so an endpoint stopped from `Ready` has already been sent up
to a prefetch batch, and a pod that starts, restarts, rolls out or scales up
while a queue is switched off consumed that batch, side effects and all
(#1289). A broker test with the stop delayed by a second showed it. Holding
deliveries in a filter while the switch is off was tried and rejected, because
`Stop` and the bus's `StopAsync` wait for in-flight deliveries without
cancelling them, so neither could finish. `AutoStart = false` does not help:
in MassTransit 8.5 it only waits for a consume pipe connection, which a
consumer configured with the endpoint already supplies, and the bus starts
every endpoint it holds. An endpoint that does not exist sends no
`basic.consume`, and connecting one later is public API that does not hold the
bus's start.
**Consequences.** The queue and its bindings are declared when the endpoint is
configured, so an endpoint off at start declares nothing until it is connected.
A queue that has never existed on the broker drops what is published to it while
it is off, and so does a binding a release adds to an existing queue: a message
of the new type routes to no queue and reaches no `_skipped` queue either, so no
alert fires. A consumer released while its endpoint is off has therefore not
been deployed in
[ADR-026](ADR-026-consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it.md)'s
sense, and the release that starts emitting waits until the switch is on and the
pod has connected the endpoint. A pod started with an endpoint off shows that
endpoint in no bus health entry, so the bus check is Healthy rather than
Degraded for it; the `operator-switches` check
([ADR-115](ADR-115-an-operator-switched-name-is-reported-by-an-operator-switches-readiness-check-that-is-degraded-supersedes-adr-113s-loop-that-adds-no-check-1013.md))
still reports it Degraded, so readiness answers the same. A name thrown in the
moment between the bus's configuration and an endpoint's readiness still lets
that endpoint take a batch before its stop lands. The bus still connects the
switch to every endpoint it configures, and one configured without the helper is
still stopped from its `Ready`, with the batch that costs; only the helper
leaves an endpoint off at start unconfigured, so a host whose test lists its
endpoints also fails for one that bypasses it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
