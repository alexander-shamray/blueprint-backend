# ADR-112 — An operator switches a receive endpoint or a worker off from a file that reloads, and no feature-flag library is adopted (#954)

**Decision.** Notifications, Shipping and Privacy, the three services
[ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md)
lets an operator stop, each read a list of names, `Switches:Off`, from a file
their pod mounts, and follow it while they run. A name is a receive endpoint's
queue or a worker loop's `ISwitchedWorker.SwitchName`. A switched-off endpoint
is stopped, which is what `ProviderKillSwitch` does: its messages wait in the
queue, and [§9.5](../09-messaging.md)'s inbox absorbs any redelivery when it
starts again. A switched-off loop skips its pass on each tick. Removing the name
undoes both, and nothing restarts. The file is rendered by the chart's
`switches.off` into a ConfigMap and mounted at `OperatorSwitchFile.DefaultPath`;
a chart ADR-104 does not class as suspendable renders none and refuses a name.
A stopped endpoint leaves the bus check Degraded, which readiness answers 200,
so the pod stays in service as it does under `ProviderKillSwitch`; a loop that
is off is logged and adds no check, because [§13.5](../13-observability.md) pins
the readiness set. No feature-flag package is adopted, and a flag that serves a
caller is not provided.
**Why.** [§15.5](../15-cicd-deployment.md) promised a kill switch that does not
require a rollback, and no host had one. An environment variable cannot be it:
a ConfigMap read through `envFrom` is read when the process starts, so changing
it is a rollout, which is the cost the promise was to avoid. A mounted
ConfigMap is updated in place by the kubelet, and the host polls it, because
the kubelet swaps the file through a symlink that inotify does not see. The
chart's pod-template checksum leaves `switches` out for the same reason, since
a value in it would roll every pod when the switch is thrown. Stopping an
endpoint, and not consuming and rejecting, keeps every message and costs the
dead-letter queue nothing. A name is a queue or a loop, not a service, because
ADR-104's bound is on how long a duty may wait, and Shipping's privacy endpoint
need not stop for its booking loop to. A name that nothing declares is logged
as a warning on each change, since a typo is likeliest in the incident the
switch is for. `Microsoft.FeatureManagement` answers whether a feature is on
for a caller, a question no host here asks: the only reader of a flag would be
a loop or an endpoint asking whether it is on.
**Consequences.** A new receive endpoint or loop needs no list updated. The bus
connects the switch to each endpoint it configures, a host test fails for an
endpoint it does not list, and a host test fails for a `BackgroundService` that
is not an `ISwitchedWorker`. A switch takes effect within the kubelet's sync
period and the poll, about a minute and not at once; an operator who needs it
sooner uses ADR-104's `suspended`, which is a rollout. The switch enforces no
bound: ADR-104's table is still the operator's to read, and a service past its
bound is an incident to declare. Four of seven services get no switch, as they
get no `suspended`. [§15.5](../15-cicd-deployment.md)'s vocabulary case takes
the split release, as it can no longer be emitted behind a flag; and the
gateway's route-level switch, which ADR-104 leaves to its own decision, is not
this one.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
