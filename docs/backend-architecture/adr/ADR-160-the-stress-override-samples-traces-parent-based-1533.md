# ADR-160 — The stress override samples traces, parent-based (#1533)

**Decision.** `AddObservability` sets no sampler in code, so the SDK's own
`OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG` decide, and their absence
means parent-based always-on: every trace is kept. Nothing sets them except
`deploy/compose/docker-compose.stress.yml`, which gives every host that
exports telemetry `parentbased_traceidratio`, at one ratio, from its
`x-trace-sampling` anchor.
**Why.** The stress run of 2026-10-28 took spans from 4 a second idle to
6 700 to 9 600 under load, and Tempo was the LGTM container's largest CPU
cost, on a machine at 96 to 98%. The observer was competing with the system it
measured. The stress run's figures come from k6, Prometheus and SQL, and
nothing in it reads Tempo, so a sampled trace store changes none of them.
Parent-based sampling keeps a trace whole or drops it whole across hosts. The
outbox's staged-trace join ([§9.4](../09-messaging.md)) carries the writer's
sampled flag to the dispatcher, so a kept trace still reaches its publish.
The SDK's variables, rather than a sampler built from a key of this
platform's, keep the code free of a setting only one override uses.
**Consequences.** During a stress run most traces are not stored, so an
exemplar or a trace link from a slow request survives only at the anchor's
ratio, and the request a reader wants may not be one. A sampler set in code
would silently outrank the variables, so `TelemetryVolumeTests` drops a root
span through the override's own sampler name. A host added to Compose without
the anchor traces in full under stress, so the same suite reads the service
files and fails until the override names it. Once the override names every
exporting host, a copy of its service list in another repository,
blueprint-admin's `StressRunService`, has to follow.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
