# ADR-138 — Notifications' erasure keeps what was written under its request (#1192), amending ADR-117 and ADR-120

**Decision.** Notifications' erasure hands its request to every subject-wide
statement but the contact's. The statements over order records, notices and
push deliveries leave an order whose record's `ErasedUnder` lists the request,
with its notices and their deliveries. `DeviceInstallations` and
`ConsentEntries` each gain `ErasedUnder`, a JSON list of the requests
Notifications had already applied to the subject when the write read its
audit rows. A registration and a preference write read them from
Notifications' own `PersonalDataErasures`, through
`IPersonalDataErasureRepository.RequestsErasingAsync`, before their batch
runs. The erasure clears a device row and anonymises an entry only when
its list does not name the request being handled, and clears the list with the
subject. A sign-out and every other clearing of a device row empty it too.
**Why.** [ADR-117](ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
and [ADR-120](ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)
make a registration or a write after an erasure the subject's own act, which a
later request removes, and
[ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)
makes an order placed under a request never that request's to erase. The
handler keyed every statement by the subject alone, so a request reissued
under the same id
([ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)),
or a late copy the inbox admits under a fresh message id, also removed what the
subject had made since its first pass. That included the records and pending
notices of an order the intake had kept. A cut-off on the audit row's time is
unsound for the reason
[ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md)
gives. The contact needs nothing: the send pass deletes a contact it saves
while any audit row names the subject
([ADR-100](ADR-100-a-send-pass-asks-its-erasure-rows-after-it-saves-a-contact.md)).
No lock is shared with the erasure, unlike ADR-135's and ADR-137's. A row can
name a request only once that request's audit row has committed, which is
after its statements ran, so a stamped row is always one the first pass could
not have cleared. A write that races the first pass reads no audit row and is
stamped under nothing. The ordering
[ADR-118](ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)
gives a device write and ADR-120 gives an append puts its write wholly before
the erasure, which clears it, or wholly after, where only a reissue clears it.
**Consequences.** Each registration and preference write scans an audit
table with no window
([ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)),
as Ordering's placement does, so the cost falls on the subject's own request
and grows with every erasure Notifications has applied; the API host now reads
that table. A write racing the first pass is cleared by a reissue as well,
which is the side that fails safe: it loses the subject a registration or a
preference they make again, and keeps nothing an erasure was for. The list
joined to a request still open names its subject, which the row already does,
so it adds a column to two rows of
[`docs/personal-data.md`](../../personal-data.md) and no holding. A row written
before the column existed carries the empty list and is cleared by any
request, as before.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
