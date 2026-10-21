# ADR-136 — An operator may amend an unclosed erasure request's responder set, recorded with who, when and why (#1177), amending ADR-092

**Decision.** A staff principal holding `privacy:erase` may add holders to, or
remove them from, the responder set of one erasure request that has not
closed, through `POST /v1/privacy/erasure-requests/{requestId}/responders`, a
keyed write command giving the names and a reason. The request keeps each
amendment, in `privacy.ErasureAmendments`, with what it added and removed,
the reason, the authenticated caller's id and the time, for as long as it keeps
the request. An answer already heard is recounted against the amended set,
so a holder added after it answered counts at once and a holder removed no
longer does. When every holder left in the set has been counted, the
amendment closes the request as a last answer would, replacing the subject's
id with its hash. Otherwise the request keeps its status and due time and
publishes nothing: the operator reissues it, under the same `RequestId`, for
a holder the amendment added to be asked. The set still never moves because
the configuration did.
**Why.** [ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
fixes the set when a request is raised, so that a configuration change
cannot close a request on fewer answers than it was raised with. It left no
way out of the opposite failure: a set naming something no holder answers
under, a misspelling of one, keeps the request overdue for ever, and a raise
for the subject returns that request unchanged. A set that omits a holder
fails the other way, closing without its answer, and an amendment adds the
holder only while the request has not closed. Two other remedies were
weighed. A reissue that re-read the configuration is the change ADR-092's
Why rules out, made silently by whoever edits the values. Superseding the
request and raising a new one would give the subject a fresh `RequestId`,
which every holder that stamps an order or an address with the requests it
was made under
([ADR-099](ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md),
[ADR-135](ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md))
would treat as a later request, erasing what the customer made after the
first. An amendment keeps the request and its id, and makes the change an
act a named person took for a stated reason on one request, which is what
an auditor asks of it.
**Consequences.** An operator can now close a request without a holder's
answer by removing that holder, so the record of who did it, and why, is
the control. It stays as long as its request, which
[ADR-095](ADR-095-a-closed-erasure-request-is-kept-as-the-evidence-and-has-no-retention-window.md)
keeps for ever as evidence on the ground that a closed row names nobody; an
amendment does name somebody, the staff user who made it, and this
decision keeps it for ever all the same, because an amendment whose author
could later be removed accounts for nothing.
[`docs/personal-data.md`](../../personal-data.md) lists it. The reason is
free text kept after the subject's id has gone, so it names holders and
never the subject; the runbook says so, and nothing enforces it. No second
person approves the change; an adopter that needs four eyes adds them in
front of the route. The amendment publishes nothing, so adding a holder needs a
reissue as a second step, and the status view shows the amendments so the
operator can see what is still missing before making it. The command is
keyed, as a reissue is, so a retry records one amendment.

**Amended by [ADR-139](ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md).**
An amendment that closes a request publishes its close, as a last answer
does, unless it or an earlier amendment removed a holder that had not answered
by the close; one that leaves it open still publishes nothing.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
