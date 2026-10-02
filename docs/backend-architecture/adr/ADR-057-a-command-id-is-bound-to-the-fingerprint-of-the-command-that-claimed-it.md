# ADR-057 — A command id is bound to the fingerprint of the command that claimed it

**Decision.** [§8.5](../08-caching-redis.md)'s `IdempotencyBehavior` stores a
successful command's result behind a fingerprint of the command that produced
it, and replays that result only to a command with the same fingerprint. The
fingerprint is `CommandFingerprint.Of`: SHA-256, in lower-case hex, over the
command serialised as the pipeline holds it, with default values omitted. It
is computed before the claim and written with the outcome, as
`sha256:{fingerprint}:{value}` in the payload
`IIdempotencyStore.CompleteAsync` already takes. On a key that is held, an
absent or unfinished entry is still `ConcurrentRequestException`; a completed
entry whose payload does not open with `sha256:` is replayed as it stands; one
whose fingerprint equals this command's, compared ordinally, is replayed; and
one whose fingerprint differs is refused with `CommandIdReusedException`,
which [§10.5](../10-api-gateway.md) answers 409 with `code`
`command.id_reused`. The handler does not run and the entry is left as it was.
**Why.** The key was bound to a subject and an operation, and to nothing the
request said. A client that sent a second, different request under a `CommandId`
it had already used was answered 200 with the first request's result, and took
its new request for applied: a success-shaped answer to work that never ran. The
command is hashed and not the HTTP body, because
[§4.2](../04-solution-structure.md) keeps HTTP out of `Common.Application`,
where the behaviour runs, and because a route value is part of what a request
asks for and no part of its body. Defaults are omitted so that a command may
gain an optional field whose absent value is its type's default without changing
the fingerprint of a request that does not send it. The fingerprint rides in the
payload because the behaviour already owns that string: the port, both Redis
scripts and
[ADR-037](ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)'s
marker row are unchanged, and no service migrates.
**Consequences.** The shape of an idempotent command is a compatibility surface,
as §8.5 already says of its result: removing, renaming or reordering a field
changes the fingerprint of requests already answered, and so does adding one
whose absent value is not its type's default, so a retry that straddles that
deploy is refused as reused. The fingerprint is of the bound command, so two
bodies that bind to equal commands match and two that bind differently do not: a
reordered list of lines is a different request, and so is an amount sent as
`10.0` where the first attempt sent `10`. A retry re-sends the bytes it sent.
The fingerprint is also of what the serialiser writes: a member declared as an
interface or as a base class other than `object` hashes only what that type
declares, and a public field, an ignored property or a member whose type exposes
no public property hashes as nothing, so two commands that differ only there
replay as one; a collection with no defined order, a hash set, may enumerate
differently in another process and refuse an honest retry. An idempotent command
holds none of them. An in-flight duplicate with different content is told
`request.in_progress` and not
`command.id_reused`, because the fingerprint is recorded with the outcome and
there is nothing to compare until the first attempt completes; recording it at
the claim would change the port, both scripts and every implementer of the port,
to move one answer one retry earlier. The marker row carries no fingerprint:
once the claim has expired, any reuse of the key is refused with
`command.already_committed` whatever it carries until the marker is purged
([§9.5](../09-messaging.md), `RetentionPolicy.IdempotencyWindow`), which is
already a refusal. A command its handler refuses, with a failed `Result`, still
stores nothing and releases its claim, so that id may then carry a corrected
request. An entry written before this record carries no fingerprint and replays
to any command for what is left of its claim's window. During a rolling deploy,
a replica on the previous release that meets a new payload fails the replay of a
command that returns a value with a 500, because it deserialises the prefix, and
replays a command that returns none as it always did, uncompared; nothing is
applied twice, and the retry is answered by this record's rule once the rollout
completes. A rollback is the same meeting for longer: the previous release
answers such a retry 500 until the entry expires, and
`command.already_committed` from then until the marker is purged. A command the
serialiser refuses now fails before its claim, where it used to run. The payload
is no longer a JSON document, and it still cannot spell the store's in-progress
state.

**Amended by
[ADR-059](ADR-059-an-entry-with-no-fingerprint-is-refused-as-already-committed.md)**,
which refuses a completed entry with no fingerprint with
`command.already_committed` rather than replaying it. The decision's clause
that such an entry is replayed as it stands, and the consequence that one
written before this record replays to any command, stand as written because
they were true when they were written, and the record that moved them is
ADR-059.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
