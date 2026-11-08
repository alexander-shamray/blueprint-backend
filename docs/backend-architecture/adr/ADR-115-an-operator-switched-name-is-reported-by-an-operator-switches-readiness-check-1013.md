# ADR-115 — An operator-switched name is reported by an operator-switches readiness check that is Degraded, supersedes ADR-113's loop that adds no check (#1013)

**Decision.** A host that registers
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)'s
switch registers one more readiness check, `operator-switches`, tagged `ready`.
It reports Degraded, naming each one, while any name is switched off, and
Healthy while none is, whether the name is a receive endpoint's queue or a
worker loop's `ISwitchedWorker.SwitchName`. Readiness answers 200 for
Degraded, so the pod stays in service exactly as it did. The check is added by
`AddOperatorSwitches`, and a host that does not call it has no such check.
**Why.** ADR-113 left a stopped endpoint visible, because the bus check goes
Degraded, and a loop that is off invisible: the pod read Healthy, and the only
trace was a warning logged once per configuration change. An operator reading
`/health/ready` after an incident could not tell a loop they had stopped from
a loop that was running. ADR-113 declined the check because
[§13.5](../13-observability.md) pinned the readiness set, and the three host
smoke tests count it. The decision that moves the set now amends §13.5 and
the three counts in the same change.
**Consequences.** Three hosts' readiness sets grow by one, and their smoke
tests count the registrations. A Degraded entry that stays in the report for
as long as a switch is thrown is the intended signal, and a dashboard that
alerts on a Degraded readiness entry will now alert for a switch an operator
threw on purpose; the entry's name is what tells the two apart. The check
reads the same file the switch does, so a name nothing declares is also
reported Degraded, which is a typo the operator wants to hear about. The check
never returns Unhealthy, because taking the pod out of service would turn a
deliberate stop into an outage, and a host whose probe must go red on a switch
needs a different mechanism than this one. ADR-113 is otherwise unchanged.
**Supersedes.** ADR-113's sentence that a loop that is off "is logged and adds
no check"; its stopped-endpoint, file and name decisions stand.

**Amended by
[ADR-147](ADR-147-a-scalar-switchesoff-refuses-the-host-at-start-and-is-reported-degraded-on-a-reload-1291-amending-adr-113-and-adr-115.md)**:
the check is also Degraded, naming the indexed form, while `Switches:Off`
holds a single value, which binds as no name and so would read Healthy; the
host refuses to start with one. It stays Degraded and never Unhealthy. This
record is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
