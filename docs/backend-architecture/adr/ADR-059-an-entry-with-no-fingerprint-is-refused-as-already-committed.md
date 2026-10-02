# ADR-059 — An entry with no fingerprint is refused as already committed

**Decision.** [§8.5](../08-caching-redis.md)'s `IdempotencyBehavior` refuses a
completed entry whose payload does not open with `sha256:` with
`CommandAlreadyCommittedException`, which [§10.5](../10-api-gateway.md)
answers 409 with `code` `command.already_committed`. The handler does not run
and the entry is left as it was. This amends
[ADR-057](ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md)
in two places and nothing else in it: its decision's clause that such an entry
"is replayed as it stands", and its consequence that "an entry written before
this record carries no fingerprint and replays to any command for what is left
of its claim's window".
**Why.** Those two sentences kept a retry alive across ADR-057's rollout, and
were needed for one `IdempotencyRetention.Window` after it. Nothing has been
deployed, so no store holds an entry written before ADR-057 and that window
has nothing in it to wait for. Left standing, the clause is a rule that any
payload not opening with `sha256:` replays to any command: ADR-057's defect
stays open for a payload that reaches the store by another route, a restored
snapshot or a hand-written key, and a later envelope under another prefix
would be read as a bare value and replayed uncompared. The refusal is
`command.already_committed` and not `command.id_reused` because it says what
is known and no more: the key's command committed, and its result cannot be
shown to belong to this request. `command.id_reused` says the request differs
from the one that claimed the key, which nothing can show of an entry with no
fingerprint, and §10.5 tells its caller that a changed request takes a new
identifier, so an honest retry answered that way could be sent again under a
new `CommandId` and applied twice. `command.already_committed` tells it to
read the resource, and it is already ADR-057's answer for the marker row, the
other record of a commit that carries no fingerprint.
**Consequences.** A key whose entry has no fingerprint refuses every command
sent under it until the entry expires, and, where its command committed a
marker, the marker refuses it from then until it is purged, so the retry that
ADR-057 replayed is now told to read the resource. A rollback to a release
before ADR-057 writes such entries, and rolling forward again answers each retry
of them `command.already_committed` for what is left of its window; nothing is
applied twice. An envelope under a prefix this release does not write is refused
the same way, so a later envelope fails closed here rather than replaying.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
