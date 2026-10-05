# ADR-069 — The migration Job is fenced by a hook policy of its own

**Decision.** The fence
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
consequences call owed to the migration Job is a second `NetworkPolicy`, which
the library chart renders with the Job, so no chart carries one without the
other. It selects the Job's pods by their own name and release, admits nothing
in, and reaches DNS and the chart's stated database peers and nothing else. It
is a `pre-install,pre-upgrade` hook weighted ahead of the Job, and its only
deletion policy is `before-hook-creation`.
**Why.** The Job's pods carry labels of their own so that no Service counts
them as endpoints, so ADR-065's policy does not select them, and Helm applies
a pre-install hook before any other object, so a policy that is not itself a
hook ahead of the Job does not yet exist when the migrator connects with the
one identity holding DDL rights ([§7.1](../07-persistence.md)). Helm may
delete a `hook-succeeded` hook while a Job past Helm's timeout still runs a
pod, so the policy is removed only by its replacement. ADRs are append-only,
so ADR-065 still reads the fence as owed, and this record is where its reader
is sent.
**Consequences.** The migrator exports no telemetry and validates no token, so
neither edge is opened; a migrator that starts to is refused wherever the
policy is enforced until this one gains the edge. Helm leaves a hook object
behind after the hook and after an uninstall, so the policy outlives every
migration; it selects only a Job's pods, so what is left fences nothing that
runs. ADR-065's caveat holds unchanged: a plugin that enforces no policy makes
this one a rendered object and nothing more.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
