# Runbook — bookings the carrier has never scanned

| | |
|---|---|
| Alert | `UnscannedShipments`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | Any `Booked` shipment past `ShipmentStats.FirstScanAge` with no scan and no cancellation awaiting the carrier's answer |
| Signal | `shipping.shipments.unscanned`, `ShipmentMetrics` in `src/Services/Shipping/Shipping.Infrastructure/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns Shipping ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

Shipping's tracking worker asks the carrier's events route what has happened
to each booked shipment. The carrier answers 404 for a booking it has not
scanned yet, and `HttpCarrierGateway.GetEventsAsync` reads that as nothing
yet, so **a route that is missing, misrouted or gone answers exactly the
same**: no poll fails, nothing backs off, and `shipping.shipments.waiting`
and `shipping.shipments.overdue` stay flat. `shipping.carrier.not_yet_known`
counts every such 404, but a healthy backlog awaiting collection raises it
too, so no threshold can be set on it alone. This alert counts the shipments
still `Booked` with no scan at all `ShipmentStats.FirstScanAge` after their
`Shipments.CreatedAt`, the order's confirmation. **For each of them no
`ShipmentDispatched` has been published**, so no customer has been told the
parcel left.

**The age is the order saga's despatch wait**, `DespatchTimeoutDelay`
([§9.6](../backend-architecture/09-messaging.md)), and that is why it is not
shorter. A booking legitimately waits for collection, and how long it waits
is the warehouse's and the carrier's schedule, which the carrier contract does
not state. The despatch wait is the one bound the platform has decided: past
it every counted order is in review, or about to be once the saga's queue
delivers the expiry: as `not_despatched`, unless it was cancelled within the
wait, which put it there as `cancelled_after_confirmation` instead. A shorter
age would be a guess at a collection time; a longer one would leave orders in
review with nothing saying why.

No `for`: the predicate is already an age, so a wait would add to it rather
than ride anything out, as the outbox lanes' rule argues. A ticket, not a
page: the saga's review row is the per-order record, and nothing is lost yet,
because every row is polled until `TrackingWorker.GiveUpAge` and each poll
reads the shipment's whole page.

## What is *not* affected

- **Booking and cancelling.** They are other routes on the carrier; a
  fulfilment pass that fails says so through `shipping.carrier.unavailable`
  and the waiting gauge.
- **A shipment whose cancellation awaits the carrier's answer.** Its parcel
  is held back, so the gauge leaves it out. One whose cancellation the
  carrier refused is moving, stays `Booked` and polled, and is counted.
- **Money and stock.** Payment and the reservation are settled, and the
  order's review row, under one of the two codes above, is where
  `order-review.md` works it.

**A shipment already despatched is affected and not counted**: if the route
has gone, its delivery 404s too and reads as nothing yet. It is still polled,
and catches up when the route answers.

## Find the cause

Two causes look identical on the gauge and need opposite responses: **the
events route has gone for every shipment**, or **these parcels were never
collected** while the route works. Whether any scan is still landing says
which:

```sql
SELECT LastScan = MAX(RecordedAt)
FROM shipping.TrackingEvents;

SELECT Id, OrderId, CarrierReference, CreatedAt
FROM shipping.Shipments s
WHERE s.Status = 'Booked'
    AND (s.CancellationRequestedAt IS NULL
        OR s.CancellationRefusedAt IS NOT NULL)
    AND NOT EXISTS (
        SELECT 1 FROM shipping.TrackingEvents e WHERE e.ShipmentId = s.Id)
ORDER BY CreatedAt;
```

- **No scan since about the oldest row's `CreatedAt`, and the count climbing
  day by day**: no shipment is being scanned, so the route, not the parcels.
- **Scans landing for other shipments, and a count that stays small**: those
  parcels were not handed over or not picked up. Still wrong, but the fix is
  the warehouse's or the carrier's, not the platform's.

To confirm a route fault, ask the carrier the same question the worker does,
for a shipment Shipping has already recorded a scan for, against
`Carrier:BaseUrl` and with the worker's own key, `Carrier:ApiKey`, from the
Secret that `carrier.apiKeySecretRef` names in
`deploy/helm/shipping/values.yaml`:

```bash
curl -i -H "Authorization: Bearer $CARRIER_KEY" \
  "$CARRIER_BASE_URL/v1/shipments/$SCANNED_REFERENCE/events"
```

A 404 for a reference the carrier has already scanned is the route: the
carrier knows the shipment. Read the response's headers: an ingress or proxy
answering in the carrier's place usually names itself there.

```promql
sum(rate(shipping_carrier_not_yet_known_total[10m]))
```

beside the polling rate says the same from the worker's side: with the route
gone, every events read is a 404, despatched shipments' included.

## Fix it

- **The route.** Restore what serves `v1/shipments/{reference}/events` — the
  carrier's own deployment, an ingress or proxy rule, or a `carrier.baseUrl`
  that points at the wrong host or path. The next poll of each shipment reads
  its whole page, so the despatches and deliveries missed in the meantime are
  recorded and published on their own; nothing is replayed by hand.
- **Parcels not collected.** Take the counted rows' references to the
  warehouse and the carrier. Shipping does nothing until a scan arrives.

Either way the count falls as scans land, and the alert resolves. **Work the
orders already in review** from `order-review.md`, by order id: under
`not_despatched`, whose row a despatch published after the saga gave up does
not clear, or under `cancelled_after_confirmation` for an order cancelled
within the despatch wait.
