# ADR-139 — An erasure check asks no request that closed before its data left the owner (#882), amending ADR-092, ADR-097, ADR-099, ADR-135, ADR-136 and ADR-138

**Decision.** Privacy publishes `PersonalDataDeleteClosed` through its outbox
when a request closes, by a last answer or by an amendment, carrying the
request's id and the close as its `OccurredAt`, unless an amendment removed a
holder that had not answered by the close: then it publishes nothing for the
request. Each holder consumes it on its
privacy endpoint and stamps that time on its own `PersonalDataErasures` row as
`ClosedAt`; a holder with no row for the request stamps nothing. Every check
that tries the audit rows' hashes against a subject is given `sourcedAt`, an
instant no later than the one its data left the owner, and asks only the rows
still open and the rows closed after `sourcedAt` less
`PersonalDataErasure.ClockMargin`. A write from an event passes the event's
`OccurredAt`; Shipping's fulfilment pass passes its shipment's `CreatedAt`,
since the address is read from Ordering after the shipment exists; a write that
stamps an `ErasedUnder` list, a placement, a saved address, a device
registration or a preference, passes its own time. A holder that finds its row
for a `PersonalDataDeleteRequested` already closed erases and reports nothing.
Notifications' send pass keeps asking every row, before the contact read and
after it.
**Why.** The measurement on #882 put the unbounded read at 200 ms and 67 MiB a
call at 100,000 rows, paid
by every booking, order event, placement and saved write. A close comes after
every holder in the set has answered, the owner included, so the owner had
anonymised the subject before Privacy closed: data it gave out after the close
cannot name the subject, unless an order says it was placed under the request,
which [ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)
already leaves out. Data it gave out before the close is still asked however
late it arrives: an outbox rebuild, or a parked message the dead-letter tool
replays at any age. The bound compares the close with the data's own instant,
and not with the holder's own row, which is the bound
[ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)
records Shipping withdrawing; the margin is what covers three clocks
disagreeing. Three others were weighed. A window after the close is unsound,
because the error queue holds a message until somebody handles it
([§9.2](../09-messaging.md)). A cache of the rows still tries every hash, which
is half the measured cost. A key on the subject is what
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s
salt rules out. The late copy is refused because a write made after the close
no longer lists the request, so a second pass would erase what
[ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md)
and [ADR-138](ADR-138-notifications-erasure-keeps-what-was-written-under-its-request-1192-amending-adr-117-and-adr-120.md)
kept. The send pass's notice records when it was written and not when its
event was, so it has no instant to give.
**Consequences.** A check reads the open rows and those closed within its
data's age and the margin, through an index on `ClosedAt`, and still tries
every row it reads. A request closed before this decision was never published,
so its rows stay open and are asked for ever until something announces them;
the send pass pays the old cost on every pass. A close is a claim about every
holder only when every holder answered: an amendment that removes one first
([ADR-136](ADR-136-an-operator-may-amend-an-unclosed-erasure-requests-responder-set-recorded-with-who-when-and-why-1177-amending-adr-092.md)),
the owner perhaps, closes the request while that holder may still give the
subject out, so such a close is never published and its rows are asked for
ever, a misspelt name removed included. Holders hear a close at different
times, so an order placed after it can leave a request off its list at
Ordering that a holder hearing more than the margin late still asks, and that
holder erases the order; that is the side that fails safe. The amendment that closes a
request now publishes, and each holder consumes a second Privacy event and
adds a column to its audit table. The holders bind the close a release before
Privacy publishes it, as [§9.2](../09-messaging.md) requires; a close a holder
misses during a rollout leaves its row open and asked, the side that fails
safe.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
