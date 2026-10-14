# ADR-121 — A sign-out above a cleared device row moves its sequence whoever sends it, and an invalid-token answer keeps the row (#1034, #474)

**Decision.** This record amends
[ADR-117](ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
twice.

**A `DELETE` acts only on a row whose subject is the caller's while the row is
live**, meaning it still holds a subject and a token. A **cleared** row is one
with no subject and no token, whatever cleared it: a sign-out, an erasure, or
a registration that took its token to another installation or passed the
subject's `DeviceTokenOptions.MaxPerSubject`. It keeps its installation id, its
sequence and its last-seen time. It belongs to no caller, so any caller's
sign-out is ordered against it as against any row. An equal sequence is the
repeat. One below the row's, or more than `DeviceTokenOptions.MaxSequenceStep`
above it, is stale and is answered 409. One above it, within that step, moves
the row's sequence and **nothing else**. The subject and the token stay empty,
and the last-seen time is not refreshed, so the stale window that ADR-117's
retention pass applies runs on from the last-seen time the row already
carries. **A sign-out that names another subject's live row writes nothing
and is answered 204 whatever its sequence**, because a 409 would hand the
caller that subject's sequence.

**A provider's answer that a token is invalid ends the delivery and leaves
the device row as it is.** FCM's `SENDER_ID_MISMATCH` and APNs'
`BadDeviceToken` and `DeviceTokenNotForTopic` are invalid-token answers. The
row is deleted only on ADR-117's unregistered answer, so
[ADR-118](ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)'s
three worker writes to a device row stay three. This withdraws the delete
that the owner's decision of 2026-10-12 on #474 gave to that answer, and the
owner confirmed the change on 2026-10-14.
**Why.** ADR-117 says both routes are `Convergent` because a retried
registration that arrives after a sign-out carries a lower sequence than the
write it would undo. That holds only if the sign-out writes its sequence.
Take a row cleared at 2. The app registers at 3 and signs out at 4, and the
sign-out arrives first. If a cleared row takes no write, the row stays at 2
and the registration at 3 is applied when it lands. The device is then
registered again after its user signed out, which is the interleaving that
ADR-117's `Convergent` declaration, inside
[ADR-058](ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md)'s
limit, says cannot land. The ownership rule exists so that one subject cannot
clear another's registration. A cleared row holds no registration to protect:
it has no subject and no token, so writing its sequence sends no push and
reveals nothing. Such a write could do harm in only two ways, and both are
bounded. It could raise the counter out of the app's reach, but
`DeviceTokenOptions.MaxSequenceStep` bounds that, and the app recovers in the
one 409 that ADR-117 already gives a counter that has fallen behind. It could
keep the row alive for ever, but leaving the last-seen time untouched rules
that out. The alternative was to keep the subject on a cleared row so that
ownership could still be checked. That would keep personal data which the
sign-out and the erasure exist to remove.

The invalid-token answers are not about one token. They come back for **every**
token when the deployment's own configuration is wrong: a sender the tokens
were not minted for, a topic that is not the app's, or a sandbox token sent to
the production endpoint. Deleting on them would turn one misconfigured deploy
into the loss of every row on that platform. That loss cannot be undone, and
it is repaired only as each app next starts, so a buyer who does not open the
app until then receives nothing. An unregistered answer is different: the
provider says the token itself is gone, not that this sender cannot use it.
**Consequences.** **A cleared row's sequence can be raised by somebody other
than the subject who cleared it**, namely anyone who knows its installation
id, which ADR-117 already calls a bearer name for its row. The app's next
registration then costs one more 409 and a resend. A sign-out that lands on a
cleared row is now a write rather than nothing.

**The ordering a cleared row keeps lasts only as long as the row.** Once the
retention pass deletes it, a registration that arrives later makes a new row,
and a device can be registered again after its user signed out. That is
outside ADR-058's limit, and this record accepts it as a residual. Because
the sign-out does not refresh the last-seen time, the row's window may end
soon after the sign-out lands, so the delay this needs can be short. The
alternative was to bound the ordering from the sign-out, but refreshing the
last-seen time is what lets a caller keep the row alive, and no other
mechanism was worth a second column.

**A token that really is invalid keeps being sent to** until the app
registers a new one or the retention pass removes the row. Each event spends a
provider call on it and records a terminal outcome. A misconfiguration
therefore shows as a burst of invalid-token outcomes on a table that is
still whole, rather than as a table that has emptied. An operator then has to
read those outcomes, because nothing deletes the rows for them.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
