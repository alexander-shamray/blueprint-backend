# ADR-063 — A worker that waits on a third party is scaled by hand

**Decision.** Shipping's and Notifications' charts — the two whose real work is
a worker waiting on a third party, a carrier and a mail relay — run no
autoscaler. Their replica count is `replicaCount` in the chart's own values
file, changed by a person and shipped by a deploy, never by `kubectl scale`,
which the next deploy undoes. **The cue is the backlog procedure.**
`QueueBacklogGrowing` and `DeliveryLagHigh` bring an operator to a consumer
that is behind;
[`queue-backlog.md`](../../runbooks/queue-backlog.md) carries the step, and for
these two it reads the gauge that sees the worker's own wait —
`shipping.shipments.overdue` and `notifications.overdue` — because their
consumers only write a row and their queues stay shallow while the work piles
up in a table. No rule pages on either gauge yet: a threshold wants a baseline
under load, and [§13.6](../13-observability.md) records that none exists.
Before raising the count, the operator reads the processor's rate limit,
because the ceiling is the third party's and not the queue's.
**Why.** CPU is the wrong signal for a host that waits on I/O: a pod blocked
on a carrier or a relay burns none, so the HTTP charts' policy would never add
a replica however far behind the work fell. Two automatic alternatives were
weighed and declined for now. *An HPA on an external metric* needs a metrics
adapter the target cluster does not run, and a threshold nobody can set
honestly without the baseline. *KEDA* needs an operator and its resources in
the cluster, with the same threshold problem, and its usual trigger — queue
depth — is the one signal that does not move here. Both would also scale
straight into the processor's limit: each replica claims its own batch per
tick (`FulfilmentWorker.ClaimBatchSize`, `TrackingWorker.ClaimBatchSize`,
`SendWorker.ClaimBatchSize`), and the claim-before-call lease that makes a new
replica safe against a repeated call does nothing for a rate limit. A person
between the signal and the replica is the one place the limit is read.
**Consequences.** Scaling waits for somebody to read a ticket, so a backlog
that builds overnight is worked down in the morning, and the gauges have to be
watched rather than paged on until the baseline exists. ADR-022's constraint
holds unchanged: neither track autoscales, and the rollout's first rung scales
the stable Deployment for its weight and, when it ends, restores the count it
found on a rollback and the promoted release's declared count on a promotion,
so a person scales between rollouts and never during one. When
[§13.7](../13-observability.md)'s load run gives the overdue gauges a baseline,
an external-metric autoscaler over them, with a ceiling set against each
processor's limit, is the candidate that supersedes this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
