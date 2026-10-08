# ADR-080 — A NetworkPolicy peer is refused wider than a /8, and an empty selector is every address

**Decision.** `commerce.networkPolicyPeers` refuses an `ipBlock` whose prefix
is shorter than /8, in either address family, a `namespaceSelector` with no
`matchLabels` and no `matchExpressions`, and an empty `podSelector` that stands
without a `namespaceSelector`; an `ipBlock` with no prefix length is refused as
not a CIDR, so the floor is never read from one. A /8 is the widest peer a
chart renders. It
amends
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md),
whose "a peer of every address fails the render" was enforced by comparing
the CIDR with `0.0.0.0/0` and `::/0` alone.
**Why.** Every address has more spellings than those two. `0.0.0.0/1` with
`128.0.0.0/1` covers all of IPv4, an empty `namespaceSelector` selects every
pod in every namespace, and an empty `podSelector` selects every pod in the
release's own. Each rendered, and each opened the egress fence ADR-065 draws on
the port it named. A rule that enumerates spellings loses to a generator of
them, so the bound is a prefix length. Eight is wide enough for every range a
deployment states here, `10.0.0.0/8` and an IPv6 `fd00::/8` included, and
narrow enough that a peer can no longer be most of the internet.
**Consequences.** A deployment that wants a range wider than a /8 cannot state
it, and the refusal names the key to narrow. A selector-based peer has to name
its namespace or its pods, which the shipped DNS and telemetry peers already
do. The floor bounds each peer and not the list: enough /8 blocks still cover
every address, and nothing refuses them, because a chart cannot tell a long
list of narrow ranges from a wrong one. `deploy/helm/smoke.sh` holds each
workload policy to exactly the addresses the values it renders state, which
catches a template that adds a peer, not a deployment that states too many.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
