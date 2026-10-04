# Runbook — an order with no owner

| | |
|---|---|
| Alert | `UnattributedOrders`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `max(bff.orders.unattributed)` > 15 minutes |
| Signal | `ProjectionMetrics`, `src/BFF/Web.Bff/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns `Web.Bff` ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

The BFF's order projection
([ADR-051](../backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md))
has a row for an order it has heard about from Payments or Shipping, and no
event from Ordering has told it whose order it is. A row with no owner is
returned by neither order route
([§10.7](../backend-architecture/10-api-gateway.md)), so **one buyer's order
history is missing an order**, and that is the whole of what anyone sees: no
error, no 5xx and no lag goes with it, which is why this gauge exists.

## What is *not* affected

- **The order itself.** Ordering holds it and the saga runs it; a cancel goes
  to Ordering rather than through this table.
- **Every other order**, this buyer's included.
- **Checkout.** Its quote reads Catalog over §9.7's hop and never this table.

**Nobody is shown somebody else's order.** That is the failure §10.7 refuses,
and an unowned row waiting here is the price of refusing it.

## The lookalike: an Ordering event that is late, not lost

§9.4 orders nothing between consumers, so a `PaymentAuthorised` or a
`ShipmentDispatched` reaching `bff-order-events` before its `OrderPlaced` is
ordinary, and creates exactly the row this gauge measures. It resolves when
the Ordering event lands, inside §13.7's event end-to-end target on a healthy
platform.

**Fifteen minutes is past every cause that resolves itself, and past the
causes that raise their own alert first.** An Ordering broker-lane stall pages
under [`outbox-broker.md`](outbox-broker.md); a backlog on `bff-order-events`,
or events reaching the BFF late, tickets under
[`queue-backlog.md`](queue-backlog.md); an Ordering event the BFF failed on
every attempt lands in `bff-order-events_error` and pages under
[`error-queue.md`](error-queue.md). **If one of those is firing, work it
first**: this alert is its symptom here, and it clears by itself once the
event is delivered. What is left when this fires alone is the silent case: the
Ordering event is not coming.

**An empty panel is not a zero.** The gauge contains a failed database read
into an absent series (§13.6's containment callout), so a flat-empty panel
and a quiet alert can mean the read is failing. The BFF logs
`Unattributed-order gauge read failed` when it is.

```promql
max by (service_name) (bff_orders_unattributed_seconds)
```

`max`, never `sum`: the gauge reads the database, so every replica exports the
same number, and the rule and this file deduplicate alike.

## Find the rows

Against the BFF's database, `Bff`:

```sql
SELECT
    OrderId,
    FirstSeenAt,
    AsOf,
    AuthorisedAt,
    RefundedAt,
    DispatchedAt,
    DeliveredAt,
    TrackingNumber
FROM bff.Orders
WHERE CustomerId IS NULL
ORDER BY FirstSeenAt;
```

`FirstSeenAt` is the BFF's clock when the row was created, which is what the
gauge measures from. The non-null columns say which events have arrived; none
of them can say whose order it is.

## Where the Ordering event is

Take the oldest `OrderId` and ask these in order. Each answer sends you to one
place.

**1. Does Ordering have the order?** Against Ordering's database:

```sql
SELECT Id, CustomerId, Status
FROM ordering.Orders
WHERE Id = @OrderId;
```

**No row** means a payment or shipment event names an order Ordering never
had: a publisher defect in Payments or Shipping, or traffic from another
environment on a shared broker. Nothing here repairs that. Record the order id
and file it against the publisher; then delete the row (below).

**2. Did Ordering stage its event, and did it leave?**

```sql
SELECT
    MessageId,
    MessageType,
    OccurredAt,
    ProcessedAt,
    Attempts,
    LastError = LEFT(LastError, 500)
FROM ordering.OutboxMessages
WHERE Lane = 'Broker'
    AND MessageType IN (
        'Common.Contracts.Ordering.V1.OrderPlaced',
        'Common.Contracts.Ordering.V1.OrderConfirmed',
        'Common.Contracts.Ordering.V1.OrderCancelled')
    AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(Payload, '$.OrderId')) = @OrderId
ORDER BY OccurredAt;
```

`MessageType` is the contract's full name and the payload keeps its property
names as declared, which is why the filter reads `$.OrderId`.

- **`ProcessedAt` null**: the event is still in Ordering's outbox. Work
  [`outbox-broker.md`](outbox-broker.md), or
  [`outbox-abandoned.md`](outbox-abandoned.md) when `Attempts` is at the cap.
- **Processed**: it left Ordering. Go to 3 with its `MessageId`.
- **No row, and the order exists**: either retention purged a processed row
  older than `RetentionPolicy.OutboxWindow`, and you are at 4 with nothing to
  replay, or Ordering never staged it, which is a defect in its mapper to
  file.

**3. Did it reach the BFF?** Against `Bff`:

```sql
SELECT MessageId, Endpoint, HandledAt
FROM bff.InboxMessages
WHERE MessageId = @MessageId;
```

- **A row**: the BFF handled the event and the order should have its owner. A
  handled Ordering event that left the row unowned is a defect in the BFF's
  projection; keep the `MessageId` and the row, and file it.
- **No row, and the message is in `bff-order-events_error`**: work
  [`error-queue.md`](error-queue.md), read the fault, fix it, replay. The
  owner arrives with the replay.
- **No row, and the message is in `bff-order-events_skipped`**: the endpoint
  had no consumer for it; work [`skipped-queue.md`](skipped-queue.md).
- **No row and nothing in either**: the event was published while
  `bff-order-events` was not there to receive it, before the BFF's consumers
  were first deployed to this environment or during a window in which the
  queue was deleted. The broker drops what no queue is bound for (§10.7). Go
  to 4.

**4. Repair from Ordering's outbox, or let the row go.**

- **The outbox row from 2 still exists**: run the rebuild tool's repair, the
  run without `--reset`, per
  [`tools/bff-replay/README.md`](../../tools/bff-replay/README.md). It sends
  every processed row in the window, not this one alone, to the BFF's queue
  only, and the BFF's inbox drops what it has already handled. **Never
  republish the row to the exchange**, which would deliver it a second time to
  every other consumer of the event.
- **It is gone**: nothing the platform still holds can attribute this order
  (ADR-051; §10.7's *An order the projection cannot yet attribute*). The row
  would stay invisible for ever and keep this alert firing. Delete it.

## Delete a row nothing will attribute

Safe, for three reasons: no route returns the row; it holds no lines, because
lines arrive only with an Ordering event and that event brings the owner too;
and if an Ordering event for it ever does arrive, the projection inserts the
row again with what that event carries; a repair run
(`tools/bff-replay/README.md`) restores the payment and shipment facts still
in their publishers' windows.

```sql
DELETE FROM bff.Orders
WHERE OrderId = @OrderId
    AND CustomerId IS NULL;
```

The `CustomerId IS NULL` guard makes the statement refuse an order that was
attributed while you were reading. Record the order id and the answer from 1
to 4 in the incident.

**After a rebuild with `--reset`, expect a few of these.** An order whose
payment or shipment events are inside the outbox window and whose `OrderPlaced`
is not is the window's edge, not a fault: the tool prints the oldest instant it
reached per publisher, and every unowned row whose order was placed before
Ordering's is one to delete.

## How to close it

The alert clears when the oldest unowned row is attributed or deleted. Run the
find query again first: it is what says the rows are gone. Then read the gauge,
which should be back to zero within a minute.

**A gauge that falls when traffic stops is not a recovery.** No payment or
shipment events means no new unowned rows, but an old row still there keeps
the age where it is: it does not clear on a quiet hour, and falls only when
the oldest row is attributed or deleted. The find query is the answer, not the
graph.
