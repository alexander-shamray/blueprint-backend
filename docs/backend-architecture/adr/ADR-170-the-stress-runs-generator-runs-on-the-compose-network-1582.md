# ADR-170 — The stress run's generator runs on the Compose network (#1582)

**Decision.** `deploy/compose/docker-compose.stress.yml` carries the stress
run's k6 as its own service, `k6`, from a pinned `grafana/k6` image under the
`stress-run` profile, so `up` never starts it and `run --rm k6` does. It
reaches the gateway, Keycloak and Prometheus by service name, and the
repository's stress scripts and `artifacts/stress/` are mounted at the paths
the run already names. `stress.js` keeps a response body only where it reads
one, the listing's. `stress/report.js`'s `GENERATOR_SERVICE` names the
container, which the sampler reads like any other and `firstToGiveUp()`
never names.
**Why.** Every stress run of 2026-10-29 ended on the generator at its first
step, the machine's CPU at 93 to 99% over the hold against
`HOST_CPU_LIMIT`'s 90. Read per process over the hold, the WSL VM took about
18 of the 24 cores, every container summed, and Windows 4 to 5 more, among
them k6 at 1.0 and Docker Desktop's port forward at 0.4, which every one of
about a thousand requests a second crossed. Four alternating runs at 3200
VUs, two each way, measured the move: outside the VM Windows fell from 4.1
and 5.1 cores to 3.4 and 3.7, the forward from 0.44 to under 0.1, and the
VM rose by k6's 0.9 to 1.0. The machine went from 22.3 and 23.0 cores to
22.3 and 22.4, so the net gain is about 0.3 of a core, inside the 0.6 by
which the two runs on Windows differed, and every run still ended on the
generator. The move is kept
for what it fixes rather than for that core: the run's k6 is the image's,
pinned and the same on any machine, no request depends on the forward, and
the generator's CPU is read beside the services' rather than guessed from
what the containers leave.
**Consequences.** The stress run at its first step still measures this
workstation as much as the platform. The platform's own containers take
about 16 of its cores, SQL Server 6 of them, so no harness change on one
machine brings that step under `HOST_CPU_LIMIT`; a second machine for k6 is
what would. Every figure taken before this decision went through the
forward, so a run after it is a new baseline and is never compared with one
before. The k6 version is the image's tag, which Dependabot moves through the
mirror in `deploy/compose/images/`; a k6 installed on the machine no longer
runs the stress run, and the SLO run is unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
