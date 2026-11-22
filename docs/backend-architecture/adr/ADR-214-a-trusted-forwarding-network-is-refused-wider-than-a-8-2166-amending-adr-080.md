# ADR-214 — A trusted forwarding network is refused wider than a /8 (#2166), amending ADR-080

**Decision.** The gateway trusts no forwarding network wider than a /8, in
either address family. The gateway chart's `templates/edge-config.yaml`
refuses an `ingress.trustedNetworks` entry whose prefix is shorter than /8,
and `Gateway.Api` refuses the same at startup, after parsing every
`Ingress:TrustedNetworks` entry there rather than when `ForwardedHeadersOptions`
is first resolved. A /8 is the widest network either renders or starts with.
This amends
[ADR-080](ADR-080-a-networkpolicy-peer-is-refused-wider-than-a-8-or-with-selectors-that-name-nothing.md),
whose floor bounded a NetworkPolicy peer and nothing else: the same width now
bounds the networks whose forwarded headers the edge believes.
**Why.** Every address in a trusted network chooses its own
`X-Forwarded-For` and `X-Forwarded-Proto`, so a network of every address is
no ingress at all: each client picks its own §10.3 rate-limit partition, the
client address the logs record and the scheme ADR-020 reads. The chart
checked each entry's shape, octets and host bits, and its smoke test asserted
that `0.0.0.0/0` rendered; the host added each entry to `KnownIPNetworks`
unchecked. The NetworkPolicy peers beside it were already bounded at /8, and
a rule that enumerates spellings of every address loses to a generator of
them, as ADR-080 argues, so the bound is the same prefix length.
**Consequences.** An overlay naming a network wider than a /8 fails the
render, and a host configured past the chart refuses to start, each naming
the key. `10.0.0.0/8` and `fd00::/8` are still trusted. The gateway's
dual-mode listener sees an IPv4 client as an IPv4-mapped IPv6 address, so an
IPv6 entry reaching `::ffff:0:0/96` is held to a /8 of IPv4 inside it, a /104:
`::ffff:0:0/96` and `::/80` would otherwise trust every IPv4 client again. The
floor bounds each entry and not the list, for ADR-080's reason: enough /8
blocks still cover every address, and nothing refuses them. The chart still
refuses IPv6 entries outright, so the IPv6 half of the floor is the host's
alone.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
