# ADR-047 — The canary judges each workload on the signals it receives

**Decision.** [§15.5](../15-cicd-deployment.md)'s analysis judges a workload
on the **signals it declares** in `deploy/canary/canary.json`, and
`read_prometheus.py` fetches those and no others. Two signals exist. `http` is
ASP.NET Core's request histogram with the probe routes excluded by an
`http_route` matcher, and it is held to both of
[§13.6](../13-observability.md)'s absolute thresholds. `consume` is
MassTransit's consume instruments — attempts, faults and duration — and it is
held to §13.6's error threshold as a fault rate, while its duration is compared
with the stable track only. Every declared signal is compared with the stable
track, and every declared signal must reach `minimumRequests` on its own; an
absent, quiet, unfetched or undeclared signal is a rollback, as is a workload
declaring none. `canary.py check` holds the declarations to the code: a service
whose tree registers a MassTransit consumer or saga declares `consume` or
carries a non-empty `consumeExemption` that argues why not, an exemption on a
service with no consumer or beside a declared `consume` fails, and the probe
exclusion must match every route `MapHealthChecks` maps, found by scanning
`src/`.

**Why.** The analysis read one HTTP histogram for every workload, filtered on
service and track alone, and that was blind in two directions. The library
chart's probes call `/health/live`, `/health/ready` and `/health/startup` often
enough to pass `minimumRequests` inside one dwell by themselves, so a canary
that served no real request was judged healthy on its own probes. And a
workload whose business work arrives on the broker was judged by none of it:
Inventory's HTTP surface is an admin route and its commands and events arrive
on queues, and Ordering's saga and command endpoints are the same for half of
its work. A release whose consumers fault on every message could climb the
whole ladder. A quiet signal resolves to a rollback rather than to a pass on
the other signal, because a workload promoted on its HTTP traffic alone has
not been observed doing the rest of its job — which is the defect, restated
for one dwell. Consume duration keeps no absolute number because no alert owns
one, and inventing a threshold for the canary alone would be the one number in
the plan nothing else agrees with; the stable-track comparison needs no owner.
The consumer rule is a scan with an argued escape rather than a list, because
a service gains a consumer by registering one, and a list is what goes stale
when it does.

**Consequences.** A consume-judged canary needs `minimumRequests` messages on
its own share of the queue within a dwell, so on a quiet day it rolls back a
healthy release; that is the price of refusing to promote on no evidence, and
it lands hardest on Inventory, whose work is almost all broker traffic. The
share itself is not the replica ratio
[ADR-022](ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)
computes: the two tracks are competing consumers on one queue, so the canary's
portion follows prefetch and processing speed, and one that fails fast can take
more than its pods' share. The fault rate counts attempts, so a message that
fails twice and then succeeds under the retry policy still counts two faults —
the stricter reading, kept deliberately. The consume series' Prometheus
spelling — the unit suffixes the OTLP mapping appends to MassTransit's `ea` and
`ms` — is not verified against a running backend, on the same terms as
ADR-022's `deployment_track` requirement: a wrong spelling matches nothing and
rolls every consume-judged rung back rather than promoting. Catalog carries
the first exemption: its one consumer is a projection fed at a rate another
service's traffic sets, faults there already reach a paging error queue, and
the reads it serves are judged by `http`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
