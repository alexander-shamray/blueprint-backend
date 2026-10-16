# ADR-130 — The options validator is held once in Common.Infrastructure (#1075), superseding ADR-128's sentence that the Ordering validator is a copy of Shipping's

**Supersedes.**
[ADR-128](ADR-128-the-sagas-five-timeout-delays-are-options-bound-from-configuration-defaulting-to-96s-constants-with-a-compose-override-that-shortens-them-1072.md)'s last sentence,
which called the validator a copy of Shipping's.
**Decision.** `AnnotatedOptionsValidator<TOptions>` is a public type in
`Common.Infrastructure.Validation`, and Shipping and Ordering register it from
there. Neither service's class library holds a copy. A service that binds
options with `[Required]` or `[Range]` registers
`IValidateOptions<T>` with it beside its `ValidateOnStart()` (§15.4).
**Why.** Two byte-identical copies differ only in namespace, and a third
service that binds validated options would add a third. Both services already
reference `Common.Infrastructure`, so holding it there adds no reference, no
pin and no row in the licence register, which is what ADR-128 declined to pay
for `ValidateDataAnnotations`.
**Consequences.** The type is now public API of a building block, so its
behaviour (every failed annotation reported, each with its message) is pinned
by `AnnotatedOptionsValidatorTests` in `Common.Infrastructure.Tests`, and a
change to it reaches every service. The package's `ValidateDataAnnotations`
stays unadopted, and the day one service needs its extra behaviour the
decision is that package's pin and register row, in place of this type.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
