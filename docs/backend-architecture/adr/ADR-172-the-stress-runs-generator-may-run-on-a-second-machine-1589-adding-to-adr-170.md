# ADR-172 — The stress run's generator may run on a second machine (#1589), adding to ADR-170

**Decision.** The stress run's k6 may run on a second machine on the same
network, from the image `deploy/compose/docker-compose.stress.yml` pins, as
[ADR-170](ADR-170-the-stress-runs-generator-runs-on-the-compose-network-1582.md)
keeps it. `deploy/compose/docker-compose.stress-remote.yml`, added after the
stress override and never alone, publishes the gateway, Keycloak and Grafana
on the LAN address `STRESS_BIND_ADDRESS` names beside loopback, and Compose
refuses it with the variable unset: the one exception to
[§14.1](../14-local-development.md)'s loopback callout. `STRESS_GENERATOR`
set to `remote` is written to `raw.json`'s plan, and `stress/report.js`'s
`ending()` then reads only a step's VU shortfall as the generator, never the
stack machine's CPU. With the knee stop, `setup()` refuses a k6 clock further
from Prometheus's than `CLOCK_SKEW_LIMIT_MS`. `stress/remote/stack.ps1` and
`stress/remote/generator.ps1` drive the two machines. Each finds its own LAN
address. The stack's refuses any network but a Private one, a firewall that
is off or admits inbound by default, and an inbound rule already naming
Docker's programs. It opens its three ports to the generator's address alone
once that machine asks for the run with the one-time token the stack prints,
which every request carries, takes `raw.json` back over its results
port, or the generator's abort when there is none, and removes its firewall
rules when the run ends; `-Restore` puts every published port back on
loopback.
**Why.** ADR-170 left the run's first step measuring the workstation as much
as the platform, and named a second machine for k6 as what would bring that
step under `HOST_CPU_LIMIT`. #1589's outbox item needs exactly that reading:
whether the outbox pass waits on the log or on CPU the saga takes. With k6
elsewhere, the stack machine's CPU is the platform's, so the rule that names
a busy machine the generator would name a saturated platform instead, and
the run would end on the `generator` where it had found the knee. The
loopback binding is the control in front of §14.1's development credentials,
so the exception is opt-in by file, scoped by the firewall to one address on
a Private network, and closed at the run's end however it ends.
**Consequences.** A run from a second machine is a new baseline, never
compared with one from a single machine: every request crosses the network
and Docker Desktop's port forward again, whose cost ADR-170 measured, and
the network's latency is in the journey's p95. k6's own CPU is no longer
sampled, so only a VU shortfall shows the generator giving out. While a run
is open, the second machine reaches Keycloak's admin console with §14.1's
default login; after it, the three ports stay bound to the LAN address with
no rule letting the network in until `-Restore`, which empties Prometheus as
the stress run's restore always does. The scripts are Windows-only and run
by no suite, so a change to them is checked by running them.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
