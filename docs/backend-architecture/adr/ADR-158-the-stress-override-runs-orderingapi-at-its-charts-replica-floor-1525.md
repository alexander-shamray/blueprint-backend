# ADR-158 — The stress override runs Ordering.Api at its chart's replica floor (#1525)

**Decision.** `deploy/compose/docker-compose.stress.yml` runs `ordering-api`
at as many replicas as the Ordering chart's `autoscaling.minReplicas`, and
resets the host port the base file publishes, which a second replica cannot
bind. The gateway reaches the service as `ordering-api:8080` and Shipping as
`ordering-api:8081`, both by Compose name. `StressOverrideTests`, in
`Ordering.Api.Tests`, holds the override's count to the chart's, so the chart
owns the number. No other service is scaled: Ordering is the one host whose own
background loop, [§9.4](../09-messaging.md)'s `OutboxDispatcher`, a stress run
has named the first to give up.
**Why.** Each replica runs an `OutboxDispatcher`, and §9.4's claim takes its
rows under `UPDLOCK, READPAST` precisely so that replicas share the table.
With one replica, the drain is one dispatcher's. The stress run of 2026-10-28,
at 3200 users, named Ordering.Api's outbox first: its oldest row grew from
17.9 s to 57.2 s over the hold. That stack ran one replica where the chart
deploys more, so the knee measured Compose's replica count as much as the
service. [ADR-157](ADR-157-the-gateways-budgets-and-the-workers-third-party-pacing-are-options-only-a-stress-override-raises-1488.md)
refused replicas as a lever for the workers' pacing, because each one spends
CPU and memory the run reads. That cost holds here too. But the run asks which
part of the platform gives out first, and a topology the chart never deploys
answers a different question. ADR-157's options are where the stress stack
departs from production. This setting brings it nearer.
**Consequences.** The extra Ordering hosts take CPU and memory from the
machine the generator shares, so the generator's own limit arrives sooner.
The sampler reports each `ordering-api` container as its own row. Every
replica exports the same outbox gauges and the run reads them with `max`, so
the backlog stays one figure. Requests to the service spread over the replicas
by Docker's DNS, across pooled connections, and Shipping's address lookups
travel over one HTTP/2 connection, so the share each replica serves is uneven.
The dispatchers' drain does not depend on that share. During a stress run,
`localhost:5101` answers nothing, so a developer reaches Ordering through the
gateway. When the chart's floor changes, the test fails until the override
follows it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
