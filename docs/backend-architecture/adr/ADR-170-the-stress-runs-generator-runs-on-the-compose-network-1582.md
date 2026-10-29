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
step, the machine's CPU at 94 to 99% over the hold against
`HOST_CPU_LIMIT`'s 90. Read per process over one such hold, the WSL VM took
17.9 to 18.1 of the 24 cores, which is every container summed, and Windows
4.4 more: k6 0.9, Docker Desktop's port forward 0.4, the `System` process's
network stack 0.4, and about 1.9 that no process carries, the hypervisor's
share of traffic crossing into the VM. A k6 on Windows sends each of about a
thousand requests a second through that forward. On the Compose network its
requests never leave the VM, and the cores they cost on the way go to the
platform instead.
**Consequences.** k6's own cores move into the VM, so the gain is the
crossing's, not k6's. Every figure the stress run took before this decision
was taken through the forward, which adds a hop to each request's latency,
so a run after it is a new baseline and is never compared with one before.
The k6 version is the image's tag, which Dependabot moves through the
mirror in `deploy/compose/images/`, and a k6 installed on the machine no
longer runs the stress run; the SLO run is unchanged. The container's CPU
appears among the busiest containers in the summary, which is the
generator's own reading, never a service's.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
