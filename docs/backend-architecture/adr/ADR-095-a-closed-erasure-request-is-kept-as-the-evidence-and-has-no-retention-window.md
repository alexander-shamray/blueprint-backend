# ADR-095 — A closed erasure request is kept as the evidence, and has no retention window

**Decision.** Privacy keeps a closed `ErasureRequest` and its
`ErasureCompletions` for ever, and `PrivacyOptions` names no retention option
for them. Nothing purges the row. The erasure design spec's retention of "a
closed request's evidence row" is withdrawn, and
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
is read to say so: its closing step replaces the subject's id with
`SHA-256(RequestId ‖ SubjectId)` and the row then holds no personal data to
bound. A pull request that adds a window adds it by a new ADR that supersedes
this one, and names the option in
[`docs/personal-data.md`](../../personal-data.md) for `RetentionMapRule`.
**Why.** The row exists to answer "was this person's erasure asked for, and
did every holder answer", and an answer that expires after a window is not
evidence. The hash is salted by the request id, so two requests for one person
do not link and the row names nobody; a retention period is the adopter's
reading of a law about personal data, and there is none here to read. A window
would also be a second clock beside the holders' audit rows, which no register
row or option gives a window either, and the two could disagree about whether
a request was ever made.
**Consequences.** `privacy.ErasureRequests` and `privacy.ErasureCompletions`
grow by one request and a handful of completions for every erasure, with no
ceiling; the volume is a staff-raised request per person erased, which a
database absorbs for the life of the deployment. An adopter whose counsel
reads a hash of a subject id as personal data has to supersede this ADR and
build the purge. An overdue
request is untouched: it keeps the id until it closes, as
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
says.

**Amended by [ADR-136](ADR-136-an-operator-may-amend-an-unclosed-erasure-requests-responder-set-recorded-with-who-when-and-why-1177-amending-adr-092.md).**
A request an operator amended keeps its amendments with it, and each names
the staff user who made it, so a closed request no longer always names
nobody. Its window is still none, on ADR-136's ground rather than this one:
the record of who changed the set is the control on the change.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
