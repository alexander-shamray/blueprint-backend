# ADR-084 — A third party's answer is held to a character set before it is recorded

**Decision.** A string that Payments' provider or Shipping's carrier supplies,
whether an approval reference, a decline or refusal code, a carrier reference,
an event id or a tracking number, is recorded only when it is printable: it
holds no control, format, line or paragraph separator, and no broken surrogate,
which are the categories Notifications' intake already refuses.
`ThirdPartyText.Recordable` in `Common.Infrastructure` holds that rule for both
adapters, and an answer that breaks it is a fault, counted and backed off, as an
over-long one already is. A tracking number, which reaches the customer, is held
further to `PlainReference.IsWellFormed` in `Common.Application`: letters and
digits in any script, with hyphens, underscores and single spaces between them,
and so no scheme, slash, dot or at sign a mail client could make a link of.
Shipping's adapter refuses a booking whose tracking number breaks it, and
Notifications' intake and the BFF's projection drop one that arrives anyway.
**Why.** Both adapters held these strings to length alone, so a provider or a
carrier could put a line break or a bidirectional override into a log line, the
payments admin view or Ordering's record, and a URL into a tracking number that
the despatch email carries from the shop's own sender to every customer. Each
party is contracted, so this is hardening rather than a hole; but a contract is
not a character set, and the adapter is [§3.1](../03-bounded-contexts.md)'s one
place that knows the wire format. The alphabet is not the contract's, since
[§4.3](../04-solution-structure.md) keeps validation out of `Common.Contracts`;
it is a building block's, which the adapter that mints the value and the two
services that render it already reference, so none of them restates it.
**Consequences.** A carrier whose tracking numbers use another character, a
slash, a dot or a plus sign, has every booking refused and backed off to its
give-up age until the alphabet is widened here, for every reader at once. The
refusal names no value, so finding the character takes the carrier's own record.
Ordering's `TrackingNumber` and `PaymentReference` keep their presence and
length checks, since their values now arrive only through adapters held to this.
Notifications' intake keeps its own printable check, the same rule on the
Application side of [§4.2](../04-solution-structure.md), so the rule has two
spellings that a change to one must carry to the other.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
