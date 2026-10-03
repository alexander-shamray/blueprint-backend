# ADR-061 — A keyed write endpoint is keyed by one command

**Decision.** A write endpoint that is keyed under
[ADR-058](ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md)
is keyed by exactly one command: the `IIdempotentCommand` it binds, or the one
it names with `Idempotent<TCommand>()`, not both. A declaration naming the
command the endpoint binds is the same command and is allowed. An endpoint
keyed by two different commands fails the build, and `WriteEndpointRule` names
it with both. This amends ADR-058's decision, which said a keyed endpoint
"dispatches an `IIdempotentCommand`" without the gate refusing a second, and
nothing else in it.
**Why.** The declaration exists for an endpoint that builds its command from a
route value and a request record rather than binding it, so beside a binding
it either repeats the bound type or names a command the endpoint does not
send. The second is what a refactor leaves behind when it changes the command
and not the declaration. The gate reads every declared command as reached, so
a stale one keeps its command off the list of idempotent commands no endpoint
reaches. That list exists to catch exactly that command, and nothing else
tells a reviewer the declaration is wrong.
**Consequences.** An endpoint that really dispatches two idempotent commands
cannot say so. It becomes two endpoints, or one command that does both, each
keyed once. None exists today: the five hosts' endpoint tables pass the rule
unchanged. The rule reads types, not behaviour, so a single declaration of the
wrong command, with nothing bound, still passes, as it did under ADR-058; a
declaration is a claim there, as `RetrySafe` is.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
