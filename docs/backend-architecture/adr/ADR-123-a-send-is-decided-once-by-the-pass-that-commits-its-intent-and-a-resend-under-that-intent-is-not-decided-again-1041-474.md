# ADR-123 — A send is decided once, by the pass that commits its intent, and a resend under that intent is not decided again (#1041, #474)

**Decision.** This record amends
[ADR-120](ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)'s
consequence on a send already being made when a withdrawal commits, and
states the rule that consequence was read from. **A send is decided once,
by the pass that commits its intent**: that pass reads the pair's
requirement, its newest entry or its default, and the intent names the entry
it was decided under. **A resend under a committed intent is not decided
again**, on either channel. A send whose intent committed and whose call then
faulted backs off with the intent kept, and a later pass resends it under
that intent without reading the journal. So **the residual after a withdrawal
is every send decided before it committed**: each notice a pass held at that
moment, which ADR-120's consequence names, and also every send whose intent
had already committed and that a later pass resends, until each ends, at the
latest when it gives up at `DeliveryOptions.GiveUpAge`.

**Why.** The intent is the record
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
says Notifications owes before a send that no key can make exactly once.
Once it commits, the send may already have reached the channel, and the
worker's resend under it is how a send whose answer was lost is made again.
Deciding the resend again would let a withdrawal end it `opted_out`:
a record saying nothing was sent, of a message that may have been. ADR-120's
own rule is that the record of every send names the entry it was decided
under, and a second decision would name one entry on the intent and another
on the outcome. The alternative was to read the journal on every pass and end
a resend opted out. It was rejected because it trades a bounded residual for
a false record, and the record is the consent evidence ADR-120 keeps.

**Consequences.** **The residual is bounded by time, not by passes**: a
message can follow a withdrawal by up to `DeliveryOptions.GiveUpAge` when it
was decided first. A preferences read can show a pair opted out while a
send decided under the entry before it is still being retried, and
[§10.11](../10-api-gateway.md) states this to the client. **The push channel
takes the rule as written**: a delivery is decided by the pass that commits
its intent, so one that waits through backoff before its first intent reads
the journal then, and a resend after it does not. No code changes for mail,
because the worker already decides a notice only while it holds no intent.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
