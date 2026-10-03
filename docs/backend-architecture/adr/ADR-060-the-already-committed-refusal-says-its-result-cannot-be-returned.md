# ADR-060 — The already-committed refusal says its result cannot be returned

**Decision.** `CommandAlreadyCommittedExceptionHandler` answers
[§10.5](../10-api-gateway.md)'s `command.already_committed` 409 with a
`detail` saying the command has been applied and its result **cannot be
returned**, and that the caller should read the resource rather than retry,
where it said the result was no longer available. This amends
[ADR-037](ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)'s
consequence that the exception "says the command was applied and its result is
no longer available, which is true of both", and nothing else in it.
**Why.** ADR-037's wording was chosen to be true of every path that raised the
exception and to name none of them. There were two then: a result never
recorded, and one recorded and expired. [ADR-059](ADR-059-an-entry-with-no-fingerprint-is-refused-as-already-committed.md)
added a third, a completed entry with no fingerprint. There the result is
recorded and still in the store, and it is refused because nothing shows it
belongs to this request. "No longer available" says the result is gone, which
is false on that path. "Cannot be returned" is true of all three and still
names no cause, so the principle ADR-037 argued for is kept and only its
wording moves.
**Consequences.** The `detail` text changes, and a client that matched on it
breaks. That is the risk RFC 9457 already assigns to parsing `detail`, and
§10.5's `code` is unchanged and remains the field to switch on. A later path
that raises this exception has to keep the wording true, and nothing checks
that for it: a test pins only that the `detail` says it cannot be returned and
not that it is no longer available.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
