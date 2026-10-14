# ADR-124 — The Notifications worker reaches each push provider an app names, on the port its address dials (#474)

**Decision.** This record amends
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
table, and with it
[ADR-118](ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)'s
sentence that the worker's row is unchanged. **`notifications` reaches each
push provider whose platform the deployment's apps name**: FCM and Google's
token endpoint while an app names `fcm`, and APNs while one names `apns`, as
it reaches the relay. It still admits nothing. The peers are one list the
deployment states, `networkPolicy.push.to`, refused empty while a provider is
called, as every third party's list is. Each port is read from the provider
address the worker is given, by ADR-065's rule for a peer whose address is not
a Secret, so an address on another port moves its rule, and a port stated at
`networkPolicy.push.port` wins. With no app, the chart renders no provider and
no rule.

**Why.** ADR-118 kept the push adapters in the worker and left its row as
written, before any adapter existed to call out. Once one does, a worker
fenced to its stores, the admin API and the relay cannot reach the provider it
was built to call, and a chart that opened the edge anyway would open one the
table owning who reaches whom does not name. The alternative was to fold the
providers into the relay's peer list. It was rejected because the relay and
the providers are different processors at different addresses, and one list
would open the relay's port to the providers' peers and theirs to the
relay's.

**Consequences.** **A deployment that names an app states the providers'
peers**: the ranges Google and Apple publish, as ADR-065 names a third party,
or the address of a proxy or egress gateway the worker dials, and never the
range a NAT translates the worker's traffic to, since a policy matches the
destination as it leaves the pod.
[ADR-080](ADR-080-a-networkpolicy-peer-is-refused-wider-than-a-8-or-with-selectors-that-name-nothing.md)
refuses a peer wider than a /8. A proxy the worker dials on a port of its own
is that port stated at `networkPolicy.push.port`, which wins. **The edge
carries the push signing keys' calls**: the RS256 assertion's exchange at
Google's token endpoint shares the list FCM's sends use, on the port its own
address dials. A third provider would join the same list, and its address
would add its port.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
