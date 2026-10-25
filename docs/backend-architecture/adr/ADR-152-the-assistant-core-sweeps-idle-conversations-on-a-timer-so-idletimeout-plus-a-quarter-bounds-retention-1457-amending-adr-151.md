# ADR-152 — The assistant core sweeps idle conversations on a timer, so IdleTimeout plus a quarter bounds retention (#1457), amending ADR-151

**Decision.** `AssistantCore` owns a timer on its `TimeProvider` that sweeps
the conversation store every quarter of `AssistantLimits.IdleTimeout`, never
more often than once a second, and it is `IDisposable`: disposing it stops the
timer. A conversation idle past `IdleTimeout` is therefore forgotten within a
quarter of `IdleTimeout` more, or a second where that is longer, whether or
not anything opens or finds a conversation. `Web.Bff`'s `AssistantRuntime` disposes the core with the host's
container. Both packages move to 0.1.1 under
[ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)'s
rule.
**Why.** [ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)
accepted that the store swept only on an open or a find, so on a replica nobody
touched, what a customer typed and what the tools read for them stayed in
memory for the process's life. `docs/personal-data.md` could state only that
tail, not a bound. A timer on the injected `TimeProvider` closes it with the
clock the tests already advance, and needs no host to schedule anything.
**Consequences.** Retention is bounded at `IdleTimeout` plus a quarter of it, or
plus a second where that is longer, not at `IdleTimeout`. A host that builds
the core and never disposes it keeps the timer, and with it the store, for the
process's life; the BFF disposes it
through its runtime. Replicas still do not share conversations, and no erasure
reaches a running replica's memory; ADR-151's other consequences stand. ADR-151
is otherwise unchanged; this decision is read beside it, as an ADR is amended
and never rewritten.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
