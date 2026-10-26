# ADR-157 — The gateway's budgets and the workers' third-party pacing are options only a stress override raises (#1488)

**Decision.** [§10.3](../10-api-gateway.md)'s two budgets become
`GatewayRateLimitOptions`, bound from the gateway's `RateLimits` section, and
the four loops that pace themselves to a third party take their batch, lease
and tick from options too: `CarrierPacingOptions` (`CarrierPacing`) for
Shipping's `FulfilmentWorker` and `TrackingWorker`, with the tracking poll
interval, and `SendPacingOptions` (`SendPacing`) for Notifications'
`SendWorker` and `PushSendWorker`. Each default is the constant it replaces,
which keeps its name and its one definition: `FulfilmentWorker.ClaimBatchSize`,
`CarrierHop.FulfilmentTick` and the rest. The claim statements take the batch
and the lease as parameters. Each section is validated at start
([§15.4](../15-cicd-deployment.md)), and a lease a pass can outlive refuses the
host. A fulfilment pass books its rows in turn, so its lease must cover the
batch times `CarrierPacingOptions.FulfilmentRowBudget`; the tracking, send and
push passes call for their rows at once, so one row's calls bound their lease
whatever the batch. No deployed value sets any of the three sections, and the
Helm chart never will. The only setter is
`deploy/compose/docker-compose.stress.yml`, which a developer adds to the
full-stack `up` for `deploy/observability/README.md`'s stress run
([§14.1](../14-local-development.md)).
**Why.** The stress run asks which part of the platform gives out first under
hundreds of concurrent users. Against the baseline stack it measures two
things that are not the software. Every k6 client shares one address, so the
`anonymous` window refuses all past `AnonymousPermitLimit` a minute. The
workers' pacing models the carrier's, the relay's and the push providers'
capacity, and the run's simulators have none: one `shipping-worker` books
`FulfilmentWorker.ClaimBatchSize` shipments a `CarrierHop.FulfilmentTick`, so
every step that places orders only grows a backlog whose cause is already
known. Two other levers were refused. Pointing the run at
service ports skips the edge, which is part of what it measures. Adding worker
replicas is [ADR-063](ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)'s
production lever, but each replica spends the host's CPU and memory, the very
resources the run reads. Options leave every production value where it was,
and each type passes §15.4's test on
[ADR-128](ADR-128-the-sagas-five-timeout-delays-are-options-bound-from-configuration-defaulting-to-96s-constants-with-a-compose-override-that-shortens-them-1072.md)'s
terms: the stress run's stack differs from production in each member the
override sets, and no deployed value is set.
**Consequences.** A configured pace is one nobody argued for production, so it
is a defect anywhere but a stress run, and the override's header says so. A
large fulfilment batch needs a long lease, so a worker killed mid-run holds
its rows for the whole of the override's `FulfilmentLeaseSeconds`. A
suite reads the override and holds its values to the validators, so a value
that would stop a host at start fails a test instead. The overdue gauges a
stress run records describe unpaced workers, so they are not the baseline under
load that ADR-063 waits on. The SLO run never uses the override, and its rate
budgets in `slo.js` stay sized to the defaults.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
