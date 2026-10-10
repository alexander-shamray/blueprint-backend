# ADR-092 — Privacy is a seventh service, and its responder set is fixed when a request is raised

**Decision.** The erasure choreography [§11.7](../11-identity-authorization.md)
draws gets its tracker as a **seventh bounded context, `Privacy`**, with a
database of its own and two hosts: an API that raises a request for a staff
principal, and a worker that consumes the completions and sweeps for an
overdue request. Its one aggregate is `ErasureRequest`, holding the subject,
the **responder set as it stood when the request was raised**, the
completions received and a status of open, closed or overdue. The responder
set is configuration read once, so a later change moves no request already
open; a completion from a name outside a request's set is recorded, flagged
and never counted. Nothing closes an overdue request by itself: an operator
reissues it under the same `RequestId` and a fresh `MessageId`, and every
consumer is idempotent on the request, not on the message.

**Each holder's step is decided here where the data map had none.** Ordering
deletes the buyer's `OrderSummaries` rows beside anonymising `Orders`;
Payments anonymises `PaymentOrders.CustomerId` and keeps the money;
Notifications deletes its order records and the subject's waiting notices
beside its contact rows, and anonymises the ended log rows; the BFF deletes
the subject's `bff.Orders` rows. Shipping's delete is
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s.
Each is one transaction in the holder's database that writes its audit row
and stages its completion through the outbox, bar the BFF, which has no
outbox and publishes directly after its commit; a crash between the two is
silence, which an overdue request exists to catch.

**The request carries the subject's id and nothing else about the subject.**
`PersonalDataDeleteRequested` holds a `SubjectId` as `OrderPlaced` holds a
`CustomerId`: an identifier, which
[ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
allows and calls personal data all the same. It adds no name, mailbox or
address. So the broker, the request's outbox row and any copy parked in an
`_error` or `_skipped` queue hold the id until they age out, exactly as they
hold it for every order event, and the choreography lists them with the other
things it cannot reach. Each holder's audit record carries
`SHA-256(RequestId ‖ SubjectId)` and never the id, so two requests for one
person do not link; Privacy replaces the id on its own row with that hash
when the request closes.

**Why.** Silence is the one outcome a choreography cannot tell from success,
and a tracker has to notice it: it needs state that outlives a process, a
clock and an escalation. An operator command that keeps that state has a
database and a retry rule and is a service without
[§4.2](../04-solution-structure.md)'s boundaries around it. A responder set
read afresh would let a configuration change close a request on fewer
answers than it was raised with, which is silence read as success by another
route.

**Consequences.** A seventh service to build, deploy and keep a broker
account for, whose only job is to be certain something happened. Its own
table holds personal data until a request closes, so it is a row in
[`docs/personal-data.md`](../../personal-data.md) and a request that never
closes keeps the id. A holder missing from the responder set fails as
silence; the journey test, not the service, is what notices. The contracts
are in `Common.Contracts.Privacy.V1`, and `PersonalDataDeleteCompleted` has
five publishers and one owner: this ADR puts it with Privacy, which consumes
it and whose responder set is its vocabulary. The
consumers ship a release before the producer, as
[§9.2](../09-messaging.md) requires, which is why the service is the fifth
of six pull requests.

**Amended by [ADR-094](ADR-094-a-holder-reports-its-erasure-by-sending-to-privacys-queue-and-does-not-publish-it.md).**
A holder sends its completion to Privacy's queue and does not stage it through
an outbox, so the paragraph above on each holder's transaction and the BFF's
exception no longer hold, and the completion has one sender per holder and not
five publishers. Notifications never had an outbox either, so the atomicity
that paragraph described held for three services and not four.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
