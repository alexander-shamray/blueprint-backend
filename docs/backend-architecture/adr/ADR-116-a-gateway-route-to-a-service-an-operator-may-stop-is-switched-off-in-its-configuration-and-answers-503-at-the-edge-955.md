# ADR-116 — A gateway route to a service an operator may stop is switched off in its configuration and answers 503 at the edge (#955)

**Decision.** A route in the gateway's route file is switched off by the
metadata entry `Suspended` set to `true`, and a switched-off route answers at
the edge, without proxying: a 503 with `Retry-After` and the `code`
`service.suspended`, which [§10.5](../10-api-gateway.md) adds to its
vocabulary. The step is the first of the proxy pipeline, so
authentication, the rate limiter and the route's authorization policy have
already run, and an unauthenticated caller meets the same 401 or 429 as on
a route that is on. The header is `SuspendedRoute.RetryAfter`, a fixed
minute: ADR-104's bound is the operator's to watch, so no time is known to
tell a client. Only a route whose cluster is in
`SuspendedRoute.SuspendableClusters` may carry the switch, which is the
services [ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md)
marks *Yes*. A route to any other cluster that sets it stops the gateway at
its first load, and a reload that sets it is refused and the configuration
before it stays. The BFF answers no read partially because of a suspension:
its one synchronous read is Catalog's quote, Catalog is not suspendable, and
every other read is from its own tables. A read added later that calls a
suspendable service states in its section whether it answers partially or
with this 503, and is placed in this ADR's table.
**Why.** Without it a switched-off service's route answers with whatever YARP
produces for a cluster with no healthy destination, which is not
[§10.5](../10-api-gateway.md)'s *Downstream dependency unavailable* row, and a
client cannot tell a planned suspension from an outage.
[§9.7](../09-messaging.md)'s third rule asks for that fallback to be decided
in advance. The switch is route metadata and not a list beside the routes,
because YARP already reloads the route file and its configuration filter is
the one place every route passes on every load, so the refusal needs no second
reader of the file. The test is
`SuspendedRouteTests`, which reads the shipped file: every route to a
suspendable cluster is switched off in turn and must answer the 503 with
nothing forwarded, and every route to another cluster must refuse the switch,
so a route added later is held to both without being named.
**Consequences.** `SuspendedRoute.SuspendableClusters` is a second statement
of ADR-104's table, which a new service must be placed in. Today only
`privacy-erase` is a route to a suspendable cluster, because Notifications and
Shipping have no external route. Nothing here delivers a changed value to a
running gateway: the switch takes effect when the configuration reloads, and
how an operator changes it without a rollout is not decided. The 503 comes
after authorization, so a caller with no permission for the route is not told
the service is off.

**Superseded in part by
[ADR-119](ADR-119-an-operator-switches-a-gateway-route-from-a-file-that-reloads-and-the-gateway-chart-renders-it-1022.md)**:
in the Consequences, "how an operator changes it without a rollout is not
decided". A mounted file the gateway follows delivers the value to a running
gateway.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
