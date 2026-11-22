# ADR-213 — An address part holds no control, format or separator character (#2166), amending ADR-107

**Decision.** An order's shipping address and a saved address hold each of
`line1`, `line2`, `city` and `postalCode` to
[ADR-084](ADR-084-a-third-partys-answer-is-held-to-a-character-set-before-it-is-recorded.md)'s
visible-text rule as well as to presence and width: a part holding a control,
format, line or paragraph separator, or a broken surrogate, is refused with a
400 by `PlaceOrderValidator` and by the BFF's `SavedAddressValidator`, which
both read `ThirdPartyText.Recordable` in `Common.Application`. The country's
shape is unchanged, and no postcode format is checked. This amends
[ADR-107](ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md),
whose saved address was held to presence, the alpha-2 shape and the widths
alone; it is still validated by the rule an order applies and by no more.
**Why.** Both validators checked presence and length, and `Address.Of` only
trims, so a customer could put a line break or a bidirectional override into
an address that a carrier's label, a screen or a log line then prints, and
forge or hide part of it, where a product's name has been held to the same
rule since
[ADR-085](ADR-085-a-published-products-name-is-visible-text-and-its-thumbnail-is-https-from-a-host-the-seller-chooses.md). The rule is the building block's already, so neither host
spells it again.
**Consequences.** An order or a saved address carrying such a character is
refused where it was accepted, and a client sees a 400 naming the part. An
address already stored is not rewritten. The aggregate's own guard in
`Address.Of` is unchanged: `Ordering.Domain` cannot reference
`Common.Application` ([§4.2](../04-solution-structure.md)), and every path to
it passes the validator first.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
