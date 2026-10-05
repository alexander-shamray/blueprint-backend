# ADR-067 — A currency's minor unit is ISO 4217's, held once

**Decision.** Money is rounded and counted at its currency's own exponent,
never at a fixed one. `CurrencyMinorUnits` in `Common.Domain` holds ISO 4217's
minor unit for every code whose exponent is not two, and answers two for every
other code: the rest of ISO's list, the codes it gives no minor unit, and a
deployment's test currency alike. Catalog's and Ordering's `Money.Of` round to it
([§5.3](../05-tactical-ddd.md)), and Payments counts the provider's integer
amount in it and refuses a command amount finer than it. No other type holds
an exponent, and nothing new writes one as a literal. This discharges the
minor-unit table
[ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
consequences call owed, and nothing else in that record.
**Why.** A fixed exponent of two is right for the pound and the tenge and
wrong for the yen, which has none, and the dinar, which has three, so a
deployment given either would charge a price nobody set. ADR-053 holds that
no type names a jurisdiction and `Address` keeps that rule by refusing to
learn any country's postcode format. The minor-unit table is the other side
of the same line: it is small, closed and published by one body, and a code
means the same thing in every deployment, while an address format is none of
those. Knowing it names no country; not knowing it is what bound the platform
to two. It is held once rather than beside each `Money`, because §4.3 keeps
the value object each context's while Catalog, Ordering and Payments must all
agree on what one unit of a currency is. A copy per context is the drift
[§4.3](../04-solution-structure.md) admits a shared bound into
`Common.Contracts` to prevent, and a domain project cannot reference that
assembly, so the table sits in `Common.Domain`, which every one already does.
**Consequences.** No column moves: every money column is `decimal(19,4)`,
whose scale holds every exponent the table answers, so none needs a
migration. The provider's integer is the one bound a four-place code can
exceed before storage does, so Payments refuses such an amount at the
command, before anything is recorded. A code missing from the table is
counted at two places, which is wrong for a currency ISO later gives another
exponent, so the table is changed when ISO changes it. The wire carries
decimals and an invariant string, so no contract changes, and Notifications
renders the event's decimal at its own scale as ADR-053 decided, so it holds
no exponent to correct.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
