# ADR-217 — The deployed collector files a pod's telemetry under the service name and track its chart states (#2166), amending ADR-022

**Decision.** Every service chart states its host's own `service.name` as
`observability.serviceName`, and `canary.py check` holds it to the descriptor's
`serviceName`, as it holds `telemetryName` (ADR-153). Each pod a chart's
Deployment runs carries two annotations under the semantic conventions' own
keys: `resource.opentelemetry.io/service.name`, the name the canary selects the
release by (its `telemetryName`, else its `serviceName`), and
`resource.opentelemetry.io/deployment.track`, the track the chart renders. The
deployed collector associates each sender with its pod by the connection alone
(`k8sattributes` with `pod_association` `from: connection`), never by a resource
attribute the sender reports, such as `k8s.pod.ip` or `k8s.pod.uid`. It deletes
the `service.name` and `deployment.track` the sender reports, then sets both
from the pod's two annotations (`extract.otel_annotations`, which is off by
default), before
[ADR-022](ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)'s copy
of the track onto the datapoint. The delete comes first because the processor
keeps an attribute the sender already set. A resource the collector cannot
associate with an annotated pod therefore keeps no `deployment.track`, so no
canary query selects its series.
**Why.** ADR-022 files a series under the track the sender's
`OTEL_RESOURCE_ATTRIBUTES` reports, and the analysis selects on that track and
on `service_name`, both of which the sender chooses, while OTLP ingest is
unauthenticated. Any pod with telemetry egress could therefore report itself as
another service's canary or stable track and pass or fail that service's rollout
(#2166, item 15). A pod's annotations are written by whoever deploys the chart,
never by the process in the pod, so a name taken from them, for a pod found by
the connection, cannot be forged by the sender. A pod found by an attribute the
sender reports could be any pod the sender names. The chart has to be told the
host's name, because the host takes it from `ApplicationName` at run time
([§13.2](../13-observability.md)) and the chart cannot read an assembly; the
descriptor already states it and the gate holds the two equal, so the value has
one owner and a checked copy. Annotations rather than labels, because a label on
the pod template is a selector a later change could start matching, and these
keys are the ones the processor's `otel_annotations` reads.
**Consequences.** The requirement on the deployed collector grows from ADR-022's
one `transform` statement to an association by connection, a delete and an
extraction, and it is still a requirement on an environment this repository does
not contain: `smoke.sh` holds the charts to rendering the annotations, and
nothing here can check that the collector honours them. A sender that reaches
the collector through another hop (an agent, a proxy) is associated with that
hop's pod, not its own, so such a hop must forward the pod's identity or be the
annotated pod itself. §14.1's local collector still trusts the sender, which is
the reach of a single-developer stack. A migration Job's pod carries no
annotations, so its telemetry keeps no track, which no canary query reads
anyway. ADR-022 is otherwise unchanged; this decision is read beside it, as an
ADR is amended and never rewritten.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
