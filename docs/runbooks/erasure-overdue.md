# Runbook — an erasure request a holder has not answered

| | |
|---|---|
| Alert | `ErasureRequestsOverdue`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any open `ErasureRequest` past its due time: `OverdueSweepService` has marked it `Overdue` |
| Signal | `privacy.erasure.overdue` and `privacy.erasure.overdue.missing` by `responder`, `ErasureMetrics` in `src/Services/Privacy/Privacy.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns Privacy ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

A staff principal raised an erasure request, every holder in the responder set
the request stored was asked to delete the subject's personal data, and at
least one has not said it did within `Privacy:CompletionSlo`
([ADR-092](../backend-architecture/adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)).
Silence is the one outcome a choreography cannot tell from success, which is
what this alert exists for. **The subject's data may still be held** by every
holder named in `privacy.erasure.overdue.missing`; the others have answered.

Nothing closes an overdue request by itself and nothing is lost by waiting:
the request keeps the subject's id, because a reissue needs it, and a holder
that answers late is still counted. A ticket, not a page, for that reason; the
service level is the statutory clock, and the adopter's reading of it is
`privacy.completionSlo` in `deploy/helm/privacy/values.yaml`.

## What is *not* affected

- **Holders that have answered.** Their audit rows carry the hash of the
  request and the subject, and their data is gone.
- **Any other request.** Each request is its own row and its own count.
- **Other alerts.** A request parked in a holder's error queue raises
  `error-queue.md`'s own alert as well as this one.

## Find the cause

Which holder is silent, and what has Privacy seen of it:

```bash
curl -H "Authorization: Bearer $STAFF_TOKEN" \
  "$GATEWAY/api/v1/privacy/erasure-requests/$REQUEST_ID"
```

The view lists the status, `missing` (the holders still to answer), and each
answer with its count; it never shows the subject. The same from the database:

```sql
SELECT r.RequestId, r.Status, r.RaisedAt, r.DueAt, r.Reissues, r.RespondersCsv
FROM privacy.ErasureRequests r
WHERE r.Status = 'Overdue'
ORDER BY r.DueAt;

SELECT RequestId, Responder, Counted, Count, ReceivedAt
FROM privacy.ErasureCompletions
WHERE RequestId = @RequestId;
```

A row with `Counted = 0` is a name outside the request's set, recorded and
flagged and never standing in for a holder: a holder missing from
`Privacy:Responders` fails as silence, and it is the same alert.

Then ask why the holder did not answer, in this order:

1. **The request never reached it.** Its queue, named `<holder>-privacy` as
   in `ordering-privacy` and `bff-privacy`, shows the delivery; a request
   parked in `_error` or `_skipped` is `error-queue.md` or `skipped-queue.md`.
2. **It ran and its answer was lost.** A holder sends
   `PersonalDataDeleteCompleted` to `privacy-completions` after its unit
   commits, so a crash between the two is silence (ADR-094). The holder's own
   audit table holds a row for the request id's hash if it ran: the table is
   `PersonalDataErasures` in the holder's own schema.
3. **Privacy cannot read it.** A completion the broker refuses to deliver, or
   one that fails mapping, is in `privacy-completions_error`.

## Fix it

Reissue the request, which asks the holders again under the same request id
and a fresh message id and resets the clock. Every consumer is idempotent on
the request, so a holder that already answered answers again with a count of
zero and Privacy keeps the larger count:

```bash
curl -X POST -H "Authorization: Bearer $STAFF_TOKEN" -H "Content-Type: application/json" \
  -d "{\"commandId\": \"$(uuidgen)\"}" \
  "$GATEWAY/api/v1/privacy/erasure-requests/$REQUEST_ID/reissue"
```

The same command id is the same reissue, so a retry broadcasts once. A closed
request answers 422: it has let the subject go and cannot be asked again.

If the holder is missing from the set, a reissue does not help, since the set
is fixed when the request is raised: raise a new request after fixing
`privacy.responders` in the deployment's values, and say on the ticket which
request it replaces. The old one stays overdue and visible, which is the
point.

The count falls as the requests close, and the alert resolves.
