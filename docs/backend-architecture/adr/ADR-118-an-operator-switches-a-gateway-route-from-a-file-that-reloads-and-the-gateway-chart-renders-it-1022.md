# ADR-118 — An operator switches a gateway route from a file that reloads, and the gateway's chart renders it (#1022)

**Decision.** The gateway layers one more configuration file over its route
file, `RouteSwitchFile.DefaultPath`, optional and followed while the gateway
runs, and an operator switches a route off by naming it in the file. The file
is YARP's own configuration: for each route it names, it sets the `Suspended`
metadata entry that
[ADR-116](ADR-116-a-gateway-route-to-a-service-an-operator-may-stop-is-switched-off-in-its-configuration-and-answers-503-at-the-edge-955.md)
reads, so a reload carries it through `SuspendableRouteFilter` like any other
change, and a route to a cluster
[ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md)
keeps on is refused whole and the configuration before it stays. The gateway's
chart renders the file from `routeSwitch.off`, a list of route ids, into a
ConfigMap it mounts as a directory, as
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)'s
chart does for a service, and leaves the key out of the pod template's
checksum, so changing it rolls no pod. The chart refuses a blank or repeated
name, and does not refuse a route to a cluster ADR-104 keeps on, because it
does not read the route file; the gateway does. No other chart carries the key.
**Why.** ADR-116 said the switch takes effect when the configuration reloads
and left open how an operator changes it on a running gateway. The route file
is baked into the image, so without this the 503 could be tested and not thrown
in an incident. A file that reloads is ADR-113's answer to the same question
for the services, which means one mechanism, one poll
(`DOTNET_USE_POLLING_FILE_WATCHER`) and one place to look. The file holds route
metadata, and not a list of names the filter would translate, because the
filter already sees every route on every load, and a list beside the routes
would need a second reader of the file with a refusal of its own. The gateway's
`suspendable` stays false: ADR-104's `suspended` stops a workload, and the
gateway is the edge.
**Consequences.** A switch takes effect within the kubelet's sync period and
the poll, about a minute and not at once, as ADR-113's does. A name that is no
route is a route with no match to YARP, so it is refused like a route to a
cluster ADR-104 keeps on: the reload fails whole, with the cause in the log,
and the gateway keeps serving as it was. That is the failure an operator is
likeliest to meet in an incident, and it looks like a switch that did nothing,
so the log is where to look; the chart can check a name's shape and not that
it is a route. The same file read when a pod starts stops that pod, so a file
the gateway refuses must be corrected before a pod is replaced.
`RouteSwitchFileTests` proves the first reload, a refused one and a name that
is no route. The route file in the image is not edited by this: a route listed
there with `Suspended` set still starts the gateway switched off, and the
mounted file wins on any route it names, for either value. ADR-116 is
otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
