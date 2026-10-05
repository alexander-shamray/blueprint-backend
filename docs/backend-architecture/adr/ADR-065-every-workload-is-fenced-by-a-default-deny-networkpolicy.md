# ADR-065 — Every workload is fenced by a default-deny NetworkPolicy

**Decision.** The library chart renders one `NetworkPolicy` per release, and
every deployable chart includes it. It selects that release's own pods — the
workload's name and its track, as the Deployment's selector does — and denies
both directions but for the edges below. The canary release renders its own
copy with the same rules, so a canary is judged inside the fence the stable
track runs in
([ADR-022](ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)).

| Workload | Admits | Reaches |
|---|---|---|
| `gateway` | The ingress controller, on `http` | Every upstream its route file names ([§10.2](../10-api-gateway.md)), on `http` |
| `catalog-api` | The gateway on `http`; `web-bff` on `grpc` | Its stores |
| `ordering-api` | The gateway on `http`; `shipping` on `grpc` ([ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)) | Its stores |
| `inventory-api`, `payments-api` | The gateway on `http` | Their stores; Payments its provider |
| `web-bff` | The gateway on `http` | `catalog-api` on `grpc` ([§9.7](../09-messaging.md)); its stores |
| `shipping` | Nothing | `ordering-api` on `grpc`; its stores; the carrier |
| `notifications` | Nothing | Its stores; the identity provider's admin API; the mail relay |

Every workload also reaches DNS, the OTLP endpoint it exports to
([§13.2](../13-observability.md)), and the identity provider, because every
host validates tokens against it ([§11.2](../11-identity-authorization.md)) and
not only the ones holding a grant. "Its stores" is whichever of a database,
the two Redis instances and the broker its values enable, and nothing else.
No ingress is opened for metrics: they are pushed over OTLP, and nothing scrapes
a pod. The kubelet's probes come from the node, which a policy never blocks.

**The selector scheme.** A peer inside the namespace is named by the
`app.kubernetes.io/name` its callers already dial as a literal, with
`app.kubernetes.io/part-of: commerce`, so it matches both of that workload's
tracks; a port is named, not numbered. Everything outside the namespace is a
list of NetworkPolicy peers the deployment states in values — an `ipBlock`, or
a namespace and pod selector for something in the cluster — and a capability
that is on with no peer stated fails the render, as does a peer of every
address, because a rule with no peer admits everyone on its port. A port is
matched at the destination pod, after a Service has translated it, so a peer's
port is its pod port. It is stated for a database, Redis and the broker, whose
addresses live in Secrets; any other peer's is read from the address the host
is already given — the authority, the OTLP endpoint, the carrier's and the
provider's base URLs, `mail.port` — unless the peer states one, which wins. A
peer behind a Service that maps its port states it; the identity provider's
covers its admin API too.
**Why.** [§11.2](../11-identity-authorization.md) says to assume the network is
hostile, and that held at each listener and nowhere between them: any pod could
reach any other, and egress was whatever the cluster allowed, while two workers
now talk to the internet and three hosts hold a client credential. A policy
written as values could be argued either way — per-pod rules by IP, a single
namespace-wide allow list, a service mesh — so the shape is recorded: names
because a workload's name is already the contract its callers dial, both ends
of every edge because a policy is enforced at each, and peers required because
a plausible default is a security decision taken for a cluster nobody has
seen, as `ingress.trustedNetworks` already argues. DNS and the OTLP endpoint
are the two peers with defaults — the cluster's DNS pods and an
`observability` namespace — because each names one place and so fails closed:
in a cluster without it the rule admits nothing, and the cost is a lookup or a
trace lost, never a wider fence.
**Consequences.** A policy is enforced only where the cluster's network plugin
enforces one; elsewhere it renders, installs and changes nothing, and nothing
here can tell which. Every deployment states its peers before this chart
installs. Egress to a hostname is not expressible in the core API, so the
carrier, the relay, the payment provider and an identity provider outside the
cluster are named by the address ranges they publish, and a provider whose
addresses move breaks the call until the values follow; a plugin's own
hostname policy is the fallback, layered by the deployment and not rendered
here. A new edge is two value edits, one at each end, or the call is refused
where it is enforced. The migration Job's pods carry labels of their own, so
they are outside this fence and owed one.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
