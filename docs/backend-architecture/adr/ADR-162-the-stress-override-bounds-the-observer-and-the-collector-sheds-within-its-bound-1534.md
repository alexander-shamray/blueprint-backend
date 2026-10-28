# ADR-162 — The stress override bounds the observer, and the collector sheds within its bound (#1534)

**Decision.** `deploy/compose/docker-compose.stress.yml` gives the observer,
both hops the platform's telemetry takes, a `cpus` and a `mem_limit` each:
the `grafana` service, the LGTM container, two cores and 8 GiB, and
`otel-collector` one core and 512 MiB. `deploy/compose/otel/config.yaml` puts
a `memory_limiter` first in every pipeline, sized as a share of the memory
the container may use, so under the override the collector refuses data
before it reaches its limit. On the default stack, which sets no limit, that
share is of the Docker machine's memory, so it binds only once the collector
itself holds most of the machine.
`TelemetryVolumeTests` finds the hops from where the hosts send their
telemetry and where the collector exports it, and fails on any the override
leaves unbounded.
**Why.** The stress run of 2026-10-28 found the LGTM container among the four
busiest containers at every step, at a mean of 129 to 172% CPU with peaks of
326%, and its memory reached 17 GB, on a machine at 96 to 98%. The run asks
which part of the platform gives out first, and an observer free to take the
machine's last cores brings the generator's limit nearer. Two cores is a
twelfth of the 24 that run's Docker had. 8 GiB is above the 6.9 GB the
container peaked at in that run's first step, when every trace was kept and
every request logged. The collector peaked at 1.24 cores and 256 MB with every
trace passing through it. [ADR-159](ADR-159-framework-categories-log-at-warning-unless-configured-1532.md)
and [ADR-160](ADR-160-the-stress-override-samples-traces-parent-based-1533.md)
cut what the observer is sent first, so a bound now caps a smaller load rather
than making a large one drop more. A collector at its `mem_limit` with no
limiter is killed with everything it holds. With one, it refuses what it is
sent, and the senders lose that batch while the collector stays up.
**Consequences.** The bound changes both services' configuration, so `up`
with the override recreates them and `up` without it recreates them again.
The LGTM container keeps its stores inside itself, so each recreate empties
Prometheus, Loki and Tempo. The first run after the override is added starts
on empty stores, and `summarise.mjs`, which reads Prometheus at each step's
hold, has to run before the stack goes back to the default profile, as the
stress run's blocks order it. A volume for the container's data was the other
way, and was not taken: it would change the default stack for a cost only a
stress run pays.
The sampler now reads the LGTM container's CPU against its two cores, so at
its cap `firstToGiveUp()` names `grafana` beside the services. That is true,
since a saturated observer delays what the run reads, but it is not a part of
the platform. The limits come from one run. A store that still grows past
8 GiB has one of its components killed by the kernel, and the next run is
what says whether the figure holds.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
