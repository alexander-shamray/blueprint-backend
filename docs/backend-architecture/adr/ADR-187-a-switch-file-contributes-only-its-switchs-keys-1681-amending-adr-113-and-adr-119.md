# ADR-187 — A switch file contributes only its switch's keys (#1681), amending ADR-113 and ADR-119

**Decision.** The two files an operator writes to switch something off while
a host runs are read for their switch and for nothing else.
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)'s
file, `OperatorSwitchFile.DefaultPath`, contributes `Switches:Off` and its
items; [ADR-119](ADR-119-an-operator-switches-a-gateway-route-from-a-file-that-reloads-and-the-gateway-chart-renders-it-1022.md)'s,
`RouteSwitchFile.DefaultPath`, contributes each route's
`Metadata:Suspended` entry under `ReverseProxy:Routes`. Every other key in
either file is left out of the configuration on every load, the first and each
reload alike, by `Common.Web`'s `SwitchFile`, and the file is not refused for
carrying one.
**Why.** Both files are layered after every other source, so the switch wins,
and both come from a ConfigMap the chart keeps out of the pod's checksum, so a
change reaches a running pod with no rollout. Read as general configuration,
whoever may write that ConfigMap, a right a namespace grants more widely than
Secret or Deployment edit, could override any setting: a gateway route's
policy, match or cluster, live, and a service's connection string, which the
Secret-sourced environment would otherwise own, or its token authority. The
charts render only the switch's shape, and the hosts enforced nothing. Leaving
a key out, rather than refusing the file, keeps a switch that is in the file
working when something else is beside it, in the incident the switch is for.
**Consequences.** A key left out is left out silently: nothing logs it, and
an operator who puts any other setting in either file sees it do nothing,
which is the intent. The scalar at `Switches:Off` that
[ADR-147](ADR-147-a-scalar-switchesoff-refuses-the-host-at-start-and-is-reported-degraded-on-a-reload-1291-amending-adr-113-and-adr-115.md)
refuses is still admitted, so that refusal still sees it. A route id that is no
route still passes the filter, and YARP refuses it as ADR-119 decides.
`OperatorSwitchFileTests` and `RouteSwitchFileKeysTests` prove both files, on
a first load and on a reload. ADR-113 and ADR-119 are otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
