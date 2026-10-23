# ADR-147 — A scalar `Switches:Off` refuses the host at start and is reported Degraded on a reload (#1291), amending ADR-113 and ADR-115

**Decision.** A single value at `Switches:Off`, from an environment variable, a
switches file or a chart, is not a list of names, and the host says so. A host
given one when it registers
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)'s
switch does not start: `AddOperatorSwitches` throws, naming the indexed form,
`Switches:Off:0` and so on. One that arrives later, in a reloaded file, is
reported by the `operator-switches` check as Degraded with the same message, not
as Healthy, and not as Unhealthy. An empty value names nothing and is let by,
and a value beside indexed children is let by, since the binder reads the
children.
**Why.** A list binds from indexed child keys only, so a scalar leaves `Off`
empty: every name reads as on, nothing throws or logs, and the check of
[ADR-115](ADR-115-an-operator-switched-name-is-reported-by-an-operator-switches-readiness-check-that-is-degraded-supersedes-adr-113s-loop-that-adds-no-check-1013.md)
reports Healthy while an operator believes a queue is stopped. A validator on
the bound options cannot see the scalar, since the binder drops it, so the raw
section is read. It refuses at start, where the host is not yet serving. It only
reports on a reload, because a validator would run on the options monitor's
change callback, where a thrown exception has no caller to observe it and the
next read of the options would throw inside every switch decision; and the
report stays Degraded because ADR-115 keeps the check from taking the pod out of
service during the incident the switch is for.
**Consequences.** A reloaded scalar is reported and not refused: the options
still bind it as none, so every switch that was off comes back on until the file
is fixed, and the signal is a Degraded readiness entry that must be read.
ADR-113 and ADR-115 are otherwise unchanged; this decision is read beside them,
as an ADR is amended and never rewritten.
