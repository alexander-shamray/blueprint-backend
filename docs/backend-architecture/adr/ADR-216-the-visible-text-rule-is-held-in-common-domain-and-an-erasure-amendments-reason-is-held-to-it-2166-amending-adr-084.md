# ADR-216 — The visible-text rule is held in Common.Domain, and an erasure amendment's reason is held to it by the aggregate (#2166), amending ADR-084

**Decision.** The character rule of
[ADR-084](ADR-084-a-third-partys-answer-is-held-to-a-character-set-before-it-is-recorded.md),
that a string is non-blank, within its bound, and holds no control, format,
line or paragraph separator and no broken surrogate, is held by
`VisibleText.Holds` in `Common.Domain`. `ThirdPartyText.Recordable` in
`Common.Application` keeps its name and callers and delegates to it.
`ErasureRequest.AmendResponders` refuses a reason that breaks the rule with a
`DomainException`, as `AmendErasureRespondersValidator` already refuses it with
a 400. This amends ADR-084, whose rule was held in `Common.Application`.
**Why.** An amendment's reason is the audit trail's, read wherever an
amendment is shown or logged, so a line break or a bidirectional override in
it could forge or hide part of the record (#2166, item 3). The validator held
it to the rule, but the aggregate held it to presence and length alone, and a
domain project may reference `Common.Domain` and nothing else
([§4.2](../04-solution-structure.md)), so the aggregate could not read the
rule where it lived. The rule is a text rule, which
[§4.3](../04-solution-structure.md) counts as shared mechanism rather than
model, so it may sit in `Common.Domain` beside the base types. A copy in
`Privacy.Domain` would give the rule two owners.
**Consequences.** `Common.Domain` grows by one type that every service's domain
can now reach, which §4.3 asks to be weighed as a coordination point: a change
to the rule moves every caller at once, as it already did through
`Common.Application`. Today the aggregate's only caller is behind the
validator, so the guard changes no answer the API gives, and it holds against
a future caller that is not. The other aggregates that keep text, such as
Ordering's `Address`, still rely on their validators, and adopting the guard
there is each one's own change.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
