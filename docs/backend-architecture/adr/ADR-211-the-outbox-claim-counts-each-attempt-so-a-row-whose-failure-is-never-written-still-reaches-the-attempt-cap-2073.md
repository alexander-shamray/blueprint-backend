# ADR-211 — The outbox claim counts each attempt, so a row whose failure is never written still reaches the attempt cap (#2073)

**Decision.** `OutboxDispatcher`'s claim statement increments `Attempts` on
every row it leases, and the fail statement no longer does: it writes
`LastError` and backs the lease off on the attempts before the one the claim
counted, so the ladder's waits are the ones they were. `OutboxClaim.Attempts`
is that counted value, and the delivery-failed log reports it as the attempt
number. A row's last attempt therefore sits at `MaxAttempts` while it is still
running, so §13.6's abandoned-rows gauge counts only rows at the cap that no
lease holds, and the fail statement clears the lease, rather than backing it
off, on a row it leaves at the cap. `MaxAttempts` and the claim's
`Attempts < @MaxAttempts` filter are unchanged
([§9.4](../09-messaging.md#94-the-transactional-outbox),
[§13.6](../13-observability.md)).
**Why.** Counted by the fail statement, an attempt was recorded only when the
dispatcher lived to write it. A fail statement that itself failed, which
#2067 logs as `BookkeepingFailed` and survives, left the count where it was,
so the row was delivered and failed again at every lease's end, past the cap,
and never reached the abandoned-row count §13.6's alert reads; a delivery
that killed its replica never reached the fail statement at all, and looped
the same way. Counting when the row is leased is the one place every attempt
passes through, so the cap holds whatever happens after the claim.
**Consequences.** A delivered row is completed with `Attempts` at one rather
than nought, and a row the operator replays reads one once it is delivered;
a climbing count with no `ProcessedAt` is still the sign the runbook names.
A row claimed by a pass that is stopped before it is delivered, whose
completion statement fails, or which a second replica reclaims when a slow
batch outlives its lease, is charged an attempt it did not fail, and comes
back after the lease rather than the ladder: ten such claims without a
delivery would abandon it, which takes ten of those landing on one row and is
the cost of not trusting the dispatcher to outlive its delivery. An abandoned
row can now carry a null `LastError`, when its attempts ended in a killed
replica or a failed write rather than a delivery's exception, and the
service's logs, not the row, then say why. A last attempt that kills its
replica or loses its fail statement is counted abandoned only once its lease
ends, `LeaseSeconds` after the claim, which is the delay the gauge accepts so
as not to page on an attempt that may yet deliver. The ladder itself is
unchanged, so
[ADR-024](ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)'s
cumulative waits still hold.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
