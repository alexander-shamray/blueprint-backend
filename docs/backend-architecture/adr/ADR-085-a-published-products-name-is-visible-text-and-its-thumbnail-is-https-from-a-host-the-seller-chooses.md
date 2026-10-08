# ADR-085 — A published product's name is visible text, and its thumbnail is https from a host the seller chooses

**Decision.** `PublishProductValidator` refuses a name holding a control,
format, line-separator or paragraph-separator character, or half a surrogate
pair: the categories Notifications' `InboundValues.Text` refuses, so the bidi
overrides and isolates, the zero-width characters and the line breaks among
them. It refuses a thumbnail URL that is not absolute `https`. It does not
restrict the thumbnail's host: a seller chooses where its image is served,
as it chooses the name and the price.
**Why.** Every buyer reads what a seller publishes, the anonymous listing
included ([§6.5](../06-cqrs.md)), and the name travels unchanged into the
published event and the BFF's projection. A name that reads differently from
what it holds, reversed by U+202E or split by a zero-width joiner, lets one
seller pass for another, and a line break inside it splits a log line. The
scheme rule already closed stored XSS through `javascript:` and `data:`; an
`http` image is mixed content on the buyer's `https` page and readable on the
wire, and nothing the platform serves needs one. A host allow-list was the
other half the issue offered: it is configuration per environment that each
host, Compose file and chart would carry, for a seller this platform already
trusts with `catalog:write`
([ADR-074](ADR-074-a-seller-reads-their-own-products-and-withdraws-one-and-a-withdrawal-is-final.md)).
**Consequences.** A buyer's browser still fetches each thumbnail from a host
the seller controls, which learns the buyer's address, agent and timing. An
adopter who grants `catalog:write` to sellers it does not trust adds either a
host allow-list to this validator or an image proxy in front of the listing,
and this ADR is what that change supersedes. A name using a zero-width joiner
legitimately, as some emoji sequences and scripts do, is refused; a seller
spells it without one.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
