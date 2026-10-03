# The buyer's order read — a projection in the BFF

Design spec, frozen at write time. No PR number: Appendix C is closed and
says a gap the plan left is a pull request whose body says so, so this is
dated and named for its subject, like the service specs beside it. Where
this document and the blueprint disagree, the blueprint wins.

**What is already decided, and where.**
[ADR-051](../../backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)
decides that the buyer's order read is served from a projection `Web.Bff`
owns, fed by eight published integration events, with no synchronous call
and no read endpoint in Shipping; it tabulates the eleven places that describe
a BFF holding no state, and says the pull request that implements the read
amends every one. [§10.7](../../backend-architecture/10-api-gateway.md) is
the wire contract: the two routes, the subject bound from the principal and
404 for an order somebody else owns, the closed status vocabulary and its
rank, the cancellation map keyed on `Origin` before `Reason`, `asOf`,
`refunded`, `cancellable`, a timestamp per status reached, money as the
server's numbers, a nullable `productName`, and the rule that a row with no
owner is never returned. [§6.6](../../backend-architecture/06-cqrs.md)
carries the callout saying the buyer's half of the history screen moved here,
and the trap that a projection keeps its rebuild script in source control
from the first day.
[ADR-045](../../backend-architecture/adr/ADR-045-the-checkout-quote-takes-quantities.md)'s
callout records that its prohibition on the BFF consuming an Ordering message
is superseded. Every event the projection reads now has a publisher: Shipping
publishes `ShipmentDispatched` and `ShipmentDelivered` through its outbox.

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. What the blueprint leaves open, and the answers

**Where the code lives.** **Two projects beside `Web.Bff`**:
`src/BFF/Web.Bff.Persistence`, a class library holding the `DbContext`, its
row types and configurations, and the migrations; and
`src/BFF/Web.Bff.Migrator`, the migrator host every service ships (§4.1).
The consumers, the projection's SQL and the read stay in `Web.Bff`, which is
the composition root (§4.2) and the only project that names MassTransit or
Dapper. The library exists for one reason: the migrator needs the schema and
must not reference a web host, which would ship the BFF inside the migrator's
image and make the migrator's dependency graph the BFF's. **Rejected**: the
`DbContext` in `Web.Bff` with the migrator referencing the host, for that
reason; and the scaffold's four projects, because §10.1 gives the BFF no
domain and no application layer to put in them, and a `Web.Bff.Application`
holding three SQL statements would be a layer by name only. It is not called
`Infrastructure`, because `Web.Bff.csproj`'s comment says the BFF has none,
and what the library holds is the schema and nothing else that word covers.

**The names.** **`Bff`**, everywhere a service's name is one: database
`Bff`, schema `bff`, the connection keys `ConnectionStrings__Bff` and
`ConnectionStrings__BffMigrator`, the broker account `bff-svc`, and every
queue it declares prefixed `bff-`. The shared `ServiceFixture` (ADR-056)
derives the database, the schema and the broker account from one name, and
`Bff` is the name that makes all three come out as the deployment spells
them. The workload, the chart, the container, the realm client and the YARP
destination stay `web-bff`: those name the host, and none of them moves.

**The rows.** **Three tables**, section 5's: `bff.Orders`, one row per order
holding a column per fact the read returns; `bff.OrderLines`, the lines an
order was placed with; and `bff.Products`, the names Catalog published. A
column per fact rather than an event log replayed on read, because §10.7's
status is a function of which facts are present and never of their order, so
the facts are all the row needs to hold.

**How rank is stored.** **As which step timestamps are set, never as a
status column.** Each step — placed, confirmed, dispatched, delivered,
cancelled — has a nullable timestamp, written once and never overwritten,
and the status is computed on read as the highest-ranked step present. That
is §10.7's rule restated as storage: a set-once column is monotonic without
a clock, a redelivery writes the value already there, and an out-of-order
arrival fills a lower step beneath a higher one without moving the status.
§10.7 fixes the rank's two ends and its one inversion — `delivered` highest,
`placed` lowest, a cancellation above a despatch — and the order that
satisfies all three is `delivered` > the cancellation members > `dispatched`
> `confirmed` > `placed`. **`delivered` stays above a cancellation** because
§10.7 says it is highest and because the case is the one where goods
reached the buyer after a compensation the despatch outran: a buyer holding
the parcel is not helped by being told the order was cancelled.

**The cancellation member is decided at write and stored.** `OrderCancelled`
carries the `Origin` and `Reason` §10.7's map reads, and nothing later can
change the answer, so the handler maps once and writes the member —
`cancelled`, `out_of_stock` or `declined` — beside the cancellation's
timestamp. The map is §10.7's table, in code, with a test per row, and a
`Reason` outside `CancelReasons`' five under a `workflow` origin reads as
`cancelled`, by the argument §10.7 gives for an absent origin: it is the
member that claims least.

**The lines.** **Written by whichever of `OrderPlaced` and `OrderConfirmed`
arrives first, and never again.** Both carry the order's lines, total and
currency, and Ordering writes them from one aggregate, so the second arrival
finds them present and writes nothing. A line is keyed by its position in
the event's list rather than by product, because a contract's line list is
the publisher's to shape and nothing in `PlacedLine` promises a product
appears once.

**What an owned row shows before its lines arrive.** §10.7 makes a row
visible the moment an Ordering event supplies its customer, and
`OrderCancelled` supplies one without lines or a total. So a row absorbed
only from `OrderCancelled` is returned with `lines` empty and `total` null,
and **the read PR adds that sentence to §10.7's field list**, because a
client meeting a null total without being told is the rendering fault
§10.7's `productName` paragraph exists to prevent. **Rejected**: hiding an
owned row until it has lines, which is a visibility rule §10.7 does not
state and the opposite of what it argues — that an order a buyer owns is
shown as far as the projection knows it.

**The list's order.** **Newest first by the instant the BFF first saw the
order, `FirstSeenAt`, with the order's id as the tiebreaker**, by Catalog's
keyset form. Not by the placement time: `OrderPlaced` may arrive after a
payment event has created the row, so a sort on it moves a row between
pages after a client has fetched them, and a keyset over a moving key
repeats or skips rows. `FirstSeenAt` is the BFF's clock at insert and never
changes. The placement time is returned for display and orders nothing.
The page clamps at a constant the read owns, and a list row carries its
lines, so the clamp times `OrderLimits.MaxLines` bounds a page's rows —
§6.6's argument for its own history query, one host out.

**Product names.** **`bff.Products`, fed by `ProductPublished` and joined on
read**, so a name is resolved at read time and never snapshotted with the
line, as §10.7 says. Its upsert guards on `OccurredAt`, which is sound here
where it is not for the order row: every `ProductPublished` is minted by
Catalog's one clock, the case §10.7 contrasts with four hosts. A product
whose `ProductPublished` predates the queue has no row and its lines read
`productName: null`, the gap §10.7 names.

**Readiness.** **SQL and the bus, and Catalog still not.** §13.5's rule is
that a host with a connection string has a readiness check, and the BFF now
has two; `ownsNoReadinessDependencies: true` goes, and the argument §4.2
makes for it is replaced rather than extended. The bus is in the set for the
reason readiness exists here: §15.1 removed the smoke stage because this
probe gates the rollout, and a release that cannot reach the broker — a
wrong credential, a missing permission — would otherwise roll out and stop
the projection with every pod reporting ready. **The cost is stated rather
than discovered**: a broker or SQL outage now takes the BFF out of rotation,
the quote with it, which is the trade every service already makes. Catalog
stays out, for the reason the chart's comment gives today.

**Authorisation.** **Authenticated, with no permission**, as the quote is.
The subject rule already scopes the read to the caller's own orders, so a
permission would separate buyers from buyers and nothing else, and §11.4
states that a permission nothing distinguishes is a dead name in the realm.
**Rejected**: an `orders:read` permission on this host, which would also
need a role in `commerce-api`, a constant, and the realm suites' closed set
widened, to grant what every authenticated principal already holds.

**The rebuild, and what it reads.** **The publishers' outboxes, not the
broker.** ADR-051 and §10.7 say the rebuild replays "from the broker's retention
window", and RabbitMQ's classic queues retain nothing once a consumer
acknowledges: the only durable copy of a delivered event is the publisher's
outbox row, kept processed for `RetentionPolicy.OutboxWindow` (§9.4). So the
rebuild is a tool that clears the BFF's orders and its own inbox rows, keeping
the product names section 8 says why, reads the eight types' processed rows from
the four publishers' outboxes, and sends each to the BFF's queue alone with its
original `MessageId` and `OccurredAt` — the facts with the times they happened,
which is §6.6's warning about a republish. **It reaches back one outbox
window**, the shortest of the four publishers', and the window stays soft; an
order older than it is not rebuilt, and recovering one is the database's backup,
which issue #446 owns. The rebuild PR states this in §10.7 and in a callout on
ADR-051, the form ADR-045's callout set, and does not rewrite either sentence.
Section 8 has the tool.

**Retention and erasure.** **No purge of the order rows**: the history is
the product, and a window that deleted a buyer's past orders would delete
the screen. `bff.Orders.CustomerId` is pseudonymous personal data, so the
erasure path is named here as ADR-052 asks beside a table's creation: delete
the subject's `bff.Orders` rows and their lines. It is §11.7's consumer
shape and owed with that extension, as Shipping's and Notifications' are.
The inbox is purged by §9.5's service on its window, and no table here
holds an address, a name or a mailbox.

**Monitoring.** **Delivery lag, which already exists, and one gauge of the
projection's own.** `IntegrationEventConsumer` records
`messaging.delivery.lag` for every message the queue delivers, and §13.6's
lag and backlog rules select every working queue, so the queue is watched
the day it is declared. What no existing signal sees is an order that never
becomes visible: a row created by a payment or shipment event whose
Ordering event never arrives. So `bff.orders.unattributed` is an observable
gauge in `shipping.shipments.overdue`'s form — a duration in seconds, the
age of the oldest row with no owner — and the deploy PR adds its rule and
its runbook. `projection.lag` gets no writer here: §13.7 argues it measures
a read model fed from the service's own outbox, and this one is fed from
four others'.

## 2. The wire shape

camelCase, as every host serialises. Both routes carry the same order
object; the detail route adds members to it.

```json
{
  "orderId": "0192f1c4-…",
  "status": "dispatched",
  "timeline": {
    "placed": "2026-10-03T09:12:44Z",
    "confirmed": "2026-10-03T09:12:51Z",
    "dispatched": "2026-10-04T15:02:10Z",
    "delivered": null,
    "cancelled": null
  },
  "refunded": false,
  "refundedAt": null,
  "cancellable": false,
  "total": { "amount": 59.97, "currency": "GBP" },
  "lines": [
    {
      "productId": "0192f1b0-…",
      "productName": "Walnut desk lamp",
      "lineTotal": { "amount": 59.97, "currency": "GBP" }
    }
  ],
  "asOf": "2026-10-04T15:02:11.204Z"
}
```

- **`status`** is §10.7's seven members, computed as section 1 says.
- **`timeline`** has one key per rank step, each the `OccurredAt` of the
  event that reached it or null. A cancellation is one key whichever of the
  three members it produced, because `status` already says which. The keys
  are fixed, so a client draws the timeline by name and never by position.
- **`refunded`** and **`refundedAt`** are §10.7's flag and timestamp.
- **`cancellable`** is true exactly when `status` is `placed` or
  `confirmed`, `Order.Cancel`'s rule read from a projection that lags it —
  §10.7's hint and not an authority.
- **`total`** is null and **`lines`** empty only for an owned row no placed
  or confirmed event has reached (section 1).
- **`lineTotal`** is quantity times unit price, computed by the server.
- **`asOf`** is the row's last write by the BFF's clock, per order.

The list route returns `CursorPage<T>` of that object — `items` and
`nextCursor` — with `cursor` and `limit` as §10.7 names them. The detail
route adds, per line, **`quantity`** and **`unitPrice`** — the latter a money
object like every other amount — and at the order:

```json
{
  "payment": {
    "authorisedAt": "2026-10-03T09:12:49Z",
    "amount": { "amount": 59.97, "currency": "GBP" },
    "refundedAmount": null
  },
  "shipment": {
    "trackingNumber": "SIM-4F2A9C",
    "dispatchedAt": "2026-10-04T15:02:10Z",
    "deliveredAt": null
  }
}
```

**`payment`** is null until `PaymentAuthorised`; a refund fills
`refundedAmount` and the order's `refundedAt`. A refund with no
authorisation recorded — §9.4 orders nothing, so it can arrive first — sets
`refunded` and shows `payment` with only the refund half. The provider's
reference is not returned: the buyer has no use for it and it is the
provider's identifier rather than the platform's. **`shipment`** is null
until either shipment event, both of which carry the tracking number.
Neither member exists on the list route.

## 3. Places the blueprint and the tree move

Each rides in the PR that makes it true; section 12 lists them by PR.

- **ADR-051's eleven rows**, each assigned in section 12, every one amended
  by the PR whose change makes its sentence false, and **each named by a set
  or a citation rather than a new number**, so the next host that gains a
  schema moves no prose: §15.2's "twelve images" becomes the images the
  services and the BFF build, and "two of the fourteen" becomes the gateway
  alone.
- **`RetentionPurgeService` takes its idempotency half as optional**, as
  Notifications made its outbox half optional: the marker table and the
  claim store together, both or neither, refused at construction otherwise.
  The BFF runs no command pipeline, so it has no markers to purge, and a
  marker table kept empty to satisfy a constructor is a table an operator
  has to be told about. §9.5 gains the sentence, in PR-1.
- **`BffFactory` gains placeholder connection strings** in PR-1 and PR-2,
  because the host now refuses to start without them, and the suite's
  container tests derive a `ServiceFixture` (ADR-056) under the name `Bff`.
  The existing quote, identity and pipeline tests keep running without a
  container: an unreachable SQL Server fails no request that does not query
  it, and MassTransit's start does not wait for the broker.
- **The broker account `bff-svc`** in `definitions.json`, held to the
  narrowest form Notifications' account set: its own `bff-` endpoints and no
  `Common.Contracts` write, because the BFF publishes nothing.
  `docs/secrets.md`'s broker sentence and local-defaults table name it,
  in PR-2.
- **Every gate that reads a messaging host's shape is told about the BFF by
  selector**, in PR-2, never by a list that names it. The one known today is
  `check_permissions.py`: it keys every account to a
  `src/Services/*/*.Infrastructure/Messaging` directory and refuses an
  account with none, so `bff-svc` fails it as a credential with no service.
  Its glob widens to the hosts' own `Messaging` directories under
  `src/BFF/`, keyed by the tree's name as a service's is — which makes the
  account `bff-svc` — and its existing `publishes` selector, a Domain
  project, already owes a host with none no contract write. PR-2's plan
  finds every other gate the same way Notifications' PR-1 did, by reading
  what each looks at, and a test whose subject is the selector proves each.
- **The rebuild tool** under `tools/`, its suite, and the README that is
  its procedure, in
  PR-4; section 8.
- **No contract moves**, **no realm object moves** — no client, no role, no
  scope — and **no gateway route moves**: §10.2's `web-bff` route already
  matches the whole namespace.

## 4. The PR sequence

Five. The schema, the consumers and the read are each reviewable alone and
each leaves the tree green; the rebuild is owed before the read is deployed
anywhere it would be needed, by ADR-051's "from the first day"; and the
watch on the projection is the last. Each row names its
[change class](../../change-locality.md); the touch set is the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(bff): the order projection's schema and migrator` — `Web.Bff.Persistence` with `BffDbContext`, the three tables and the inbox, the first migration; `Web.Bff.Migrator`; the host registering the context, the SQL readiness check and the inbox purge, and dropping its readiness exemption; `RetentionPurgeService`'s optional idempotency half; `BffFactory`'s placeholder keys and the suite's `ServiceFixture`; the Compose migrator and SQL keys; `ci.yml`'s `bff-migrator` image; the solution file; the chart's `database` block, migrator image and migration job, the descriptor's `migrator: true`, and the databaseless wording in `smoke.sh` and `canary.py`; §14.2's database half; `docs/repo-map.md`'s BFF entry; the secret scan's allow entries for the printed local defaults | A+D+E |
| 2 | `feat(bff): the eight consumers that keep the projection` — `bff-order-events`, the handlers, the rank and the cancellation map, the product names, `bff.orders.unattributed`; the broker account `bff-svc`, `check_permissions.py`'s widened glob and `broker-permissions.yml`'s path filter; the Compose broker key; `docs/secrets.md`'s rows; the chart's `broker` block and the descriptor's `consume` signal; §14.2's broker half | A+D+E |
| 3 | `feat(bff): GET /v1/orders and /v1/orders/{id}` — the two routes, the reader, the cursor, the response types, the subject binding and the 404, the page clamp, §10.7's sentence on an owned row with no lines | A+D |
| 4 | `feat(tools): the order projection's rebuild` — the tool, its suite, its README, its solution entry and the output gate's walk of `tools/`, §10.7's sentence and ADR-051's callout on the window it reaches, the `tools/bff-replay/` line in §4.1's tree, `docs/repo-map.md` and `CLAUDE.md` | A+D+E |
| 5 | `feat(deploy): the unattributed-order rule, its runbook and its panel` — the rule over `bff.orders.unattributed`, the runbook it maps to, the dashboard panel | D |

**Order.** Strictly 1, 2, 3, 4, 5. PR-3 could follow PR-1 in principle, since
a read over empty tables is testable, and it does not: a route whose rows
nothing writes is a contract with no behaviour behind it, and its tests
would seed the tables by hand in shapes the consumers never produce. PR-4
needs PR-2's queue name and handlers; PR-5 needs the gauge PR-2 adds.

**Each gets its own issue, and #425 is closed by the last**, as #425 asks.
The plan PR closes nothing.

**Why the schema lands before anything writes it** is Payments' and
Notifications' argument: the migrator's proof is a database that migrates
and a host that starts against it, and that is reviewable before the SQL
that fills it.

**Why each chart half rides the PR that makes the host need it**, where
the services' specs deferred their charts. The BFF's descriptor and chart
exist already, so the gates that read them read the new code the day it
lands: `smoke.sh` greps the descriptor's source and refuses a chart whose
host reads a connection the chart does not name, and `canary.py`'s consumer
scan refuses a host with an `AddConsumer` whose descriptor declares no
`consume` signal. Under both gates is the reason they exist: a release built
from `main` after PR-1 would otherwise start a pod that refuses to start. So
PR-1 carries the chart's database half and its migrator, and PR-2 its broker
half and the signal. `ci.yml`'s `bff` filter already matches `src/BFF/**`,
so the new projects are built and tested the day they land.

## 5. Persistence

Schema `bff`, database `Bff`, both connection keys. Three tables beside the
inbox, and no outbox and no idempotency markers:

| Table | Key | Holds |
|---|---|---|
| `Orders` | `OrderId` | `CustomerId NULL`, `Currency NULL`, `TotalAmount NULL`, `PlacedAt NULL`, `ConfirmedAt NULL`, `DispatchedAt NULL`, `DeliveredAt NULL`, `CancelledAt NULL`, `CancelOutcome NULL`, `AuthorisedAt NULL`, `AuthorisedAmount NULL`, `RefundedAt NULL`, `RefundedAmount NULL`, `PaymentCurrency NULL`, `TrackingNumber NULL`, `FirstSeenAt`, `AsOf` |
| `OrderLines` | `(OrderId, LineNumber)` | `ProductId`, `Quantity`, `UnitPrice` |
| `Products` | `ProductId` | `Name`, `PublishedAt` |

**Every `Orders` column but the key and the two BFF instants is nullable**,
because any of the seven order events can create the row and each knows only its
own facts; `ProductPublished` feeds `Products` alone. `FirstSeenAt` is set at
insert and `AsOf` at every write that changes the row, both from the registered
`TimeProvider`. The read's index is `(CustomerId, FirstSeenAt DESC, OrderId
DESC)`, filtered to `CustomerId IS NOT NULL`, so the keyset seek never reads an
unowned row; the gauge's is `FirstSeenAt` filtered to `CustomerId IS NULL`,
which PR-2 adds with the gauge. **`PaymentCurrency` is the payment events'
own**, written by whichever of `PaymentAuthorised` and `PaymentRefunded` arrives
first and labelling both amounts, because a payment event can create the row
before Ordering's `Currency` exists and an amount without its currency is a
number the client cannot render. A payment event whose currency does not fit the
column writes neither, and is logged at warning with the order's id: a
constraint holds the two together, and dropping the amount is better than
storing a number with no label. `CancelOutcome` is a bounded string holding one
of the three members, and a check constraint holds it to them. Amounts are
`decimal(19,4)`, the precision every service's money columns take; widths come
from constants in the persistence project, which the configurations and the
migration both read.

**`CustomerId` is written once, by the first Ordering event, and never
changed.** All three carry it, and a later one disagreeing would be a
publisher defect; the handler leaves the existing value and logs the
mismatch at warning, with both order and customer ids and nothing else,
rather than move an order between buyers.

**The migrations are named for their content and emitted by `dotnet ef
migrations add`**: `AddOrderProjection` in PR-1, and
`IndexUnattributedOrders` in PR-2 for the gauge's index. A first migration
rewritten in review is followed by `down -v` before the migrator's answer
is believed.

## 6. Messaging

**`bff-order-events`** is one queue binding the eight events, declared as
§9.5 prints a receive endpoint: the inbox filter outside the in-memory
outbox, and a standard retry ladder of the BFF's own, by Notifications'
endpoint shape. **No delayed redelivery**: every handler writes rows in the
BFF's database and calls nothing, so no consumer meets a fault that is a
wait. One queue, because the eight share every property a queue would
separate, and eight would be eight backlog series and eight `_error` queues
for one failure mode.

**Each handler is one statement in one transaction**, by
`ProductPriceProjection`'s form (§6.6): Dapper over the BFF's connection
factory, `SET XACT_ABORT ON`, a `MERGE ... WITH (HOLDLOCK)` that inserts the
row when it is missing — §10.7's insert-on-missing — and otherwise sets only
the columns that are still null, then `COMMIT`. The inbox row commits after
it in the filter's own save, and a crash between the two redelivers a
message whose second application writes nothing, because every column it
sets is already set. **That is why the statements set and never overwrite**:
idempotence is what makes the two commits safe, and it is the same property
that makes the rank need no clock.

**`OrderCancelled` before `OrderPlaced`** is the ordinary interleaving
§9.4 allows, and it commutes: the cancellation creates the row with its
customer and its member, and the late placement fills the lines, the total
and the placement time and leaves the cancellation alone. A test runs every
pair of the three Ordering events in both orders, and every shipment and
payment event before and after the Ordering event that attributes it.

`MessagingRegistrationTests` asserts every event §3.2's new BFF row names
has an `AddConsumer` and a handler, and the broker-binding test over a live
broker that each is bound to `bff-order-events`. The account `bff-svc`
writes its own endpoints and the fault exchanges and **no `Common.Contracts`
exchange**, as `notifications-svc` does. `check_permissions.py` reaches it
through section 3's widened glob, and owes it no contract write through the
`publishes` selector it already has.

## 7. The read

**Two endpoints on one group**, `MapGroup("/v1/orders")` with
`RequireAuthorization()` at the group, as checkout's is. The subject is
`ICurrentUser.Id` and nothing in the request names it.

- **`GET /v1/orders`**: `cursor` and `limit`; the limit clamped to the
  read's constants; one seek over the owned index fetching one row more than
  the page to say whether a next page exists, Catalog's form; the lines and
  names for the page's orders in one second query, keyed by the page's ids
  as one parameter, so a page is two round trips whatever its size. The
  cursor is `Common.Application`'s `Cursor` over `(FirstSeenAt, OrderId)`,
  the encoding Catalog's list and §6.6's query already share, and a cursor
  that does not decode serves the first page, as that type decides.
- **`GET /v1/orders/{id:guid}`**: one row by id **and** customer, so a row
  owned by somebody else and a row owned by nobody both answer 404 by the
  same query, never by a second read that could tell them apart. Its lines
  with quantity and unit price, and the payment and shipment members.

**Neither declares a retry safety**: ADR-058's rule selects the write methods, a
GET is outside it, and Catalog's list declares nothing either; a test holds both
routes outside the rule's writes. The 404 goes through §10.5's error shape, and
neither route spends §9.7's hop budget: the read makes no call at all, which is
ADR-051's decision and a test asserts — the pricing client's handler records no
request during either route.

## 8. The rebuild

**A console tool, `tools/bff-replay`, and the README beside it that says when to
run it.** Not a runbook: `deploy/observability/check.py` holds every file under
`docs/runbooks/` to an alert that maps to it, and a rebuild is an operator's
decision no alert makes; PR-5's runbook links to the README. It is a .NET
project rather than a script because the outbox's `MessageType` column holds
`MessageTypeMap`'s names and its `Payload` the outbox's serialiser's output, and
reading both correctly means referencing the code that wrote them rather than
reimplementing either in a second language. It references `Common.Contracts`,
`Common.Infrastructure` and `Web.Bff.Persistence` for the schema it clears, and
nothing of any service. **It joins `Platform.slnx`**, so the build, the
analysers and `dotnet format` read it, and the output gate, which refuses a
solution entry outside the trees it walks, walks `tools/` too.

**What it does, in order**, each step refusing to continue if the one
before it did not complete:

1. Takes the BFF's connection, the broker's, and one read-only connection
   per publisher — Catalog, Ordering, Payments, Shipping — from its
   environment, and refuses to start with any missing.
2. Opens every connection and probes each outbox and the broker, so that
   nothing is deleted by a run that would then fail to read or send.
3. Deletes the BFF's order and line rows and its inbox rows for
   `bff-order-events`, in one transaction, when run with `--reset`; without
   it, replays over what is there, which the handlers' idempotence makes
   harmless and the inbox makes partial — an operator repairing a gap leaves
   the inbox, and one rebuilding from nothing clears it. **`bff.Products`
   is kept**: Catalog publishes `ProductPublished` once per product, so a
   name older than its outbox window would be lost for good, and the
   table's `OccurredAt` guard makes a replay over it harmless.
4. Reads each publisher's processed `Broker`-lane rows whose `MessageType`
   is one of the eight, oldest first.
5. Sends each to `queue:bff-order-events` alone — never published to the
   exchange, which would redeliver to every other consumer — with its
   `MessageId`, `CorrelationId` and `OccurredAt` as the row holds them.
6. Prints the count per type and the oldest `OccurredAt` it found per
   publisher, which is the window the rebuild actually reached.

**The window is the README's first paragraph**: a rebuild restores what the
shortest outbox window still holds, and an order whose every event is older
than that stays absent after `--reset`. So the README says to repair before
it says to rebuild, and to restore the database when the loss is older than
the window.

## 9. Configuration and deployment

**PR-1's keys** are `ConnectionStrings__Bff` and
`ConnectionStrings__BffMigrator`; **PR-2's** is `ConnectionStrings__RabbitMq`
with the `bff-svc` account. Compose gains the migrator as the services' pair
rule draws it: SQL is the migrator's dependency, and the host depends on the
migrator completing, in PR-1, and on RabbitMQ, in PR-2. **The broker password
is the one new secret, and it is PR-2's**, and it is not one of
`docs/secrets.md`'s five places: RabbitMQ imports its accounts from
`definitions.json` with no environment override, which that file already
says, so it takes the broker sentence and the local-defaults row
`notifications-svc` has. The SQL keys use the shared SQL login every
service's local default uses.

**The chart moves in two halves**, section 4 says why. PR-1 turns on the
library chart's `database` capability, adds the migrator image and the
migration job every service chart renders, rewrites the values file's
comments that say the BFF has no connection, and sets the descriptor's
`migrator: true`; PR-2 turns on `broker` and gives the descriptor the
`consume` signal beside `http`, which ADR-047's analysis reads with no
change. The readiness set is SQL and the bus (section 1), and the chart's
probes are unchanged. **The local defaults are printed where Compose and
§14.1 print every service's**, and each takes its entry in the secret
scan's allow list for that tree; this spec and its plans print none,
because the scan reads `docs/superpowers/` too.

## 10. Observability

**`bff.orders.unattributed`**, on a `Web.Bff.Projection` meter: an
observable gauge, a duration in seconds, the age by the registered clock of
the oldest `bff.Orders` row whose `CustomerId` is null, and zero when there
is none; exported as `bff_orders_unattributed_seconds`. §13.2's export names
meters one by one, so PR-2 adds the `AddMeter` line. The rule and its
runbook are PR-5's: the threshold is minutes, not seconds, because a payment
event beating `OrderPlaced` is ordinary and resolves itself, and the runbook
says what to look for when it does not — Ordering's outbox, the event's
`_error` queue, or an `OrderPlaced` older than the queue.

**No log line carries anything but ids**: the order's, the customer's and
the event's. No table here holds personal data beyond the customer's id, and
the logs hold no more than the tables.

## 11. Testing

By [§12](../../backend-architecture/12-test-strategy.md)'s layers, in the
one suite `Web.Bff.Tests`, with `Web.Bff.TestSupport` beside it; the
container tests are `Category=Integration` and never skipped.

- **Without containers**: the rank over every subset of the five steps; §10.7's
  cancellation map, a row per line of its table plus an unknown reason under
  each origin; `cancellable` per status; an undecodable cursor serving the first
  page; `RetentionPurgeService`'s optional half refused when only one of the
  pair is supplied. - **Over SQL Server**, through the real migrator: each
  handler against an empty table and against every row shape another handler
  leaves; redelivery writing nothing; every pair of events in both orders, as
  section 6 lists; a disagreeing customer left alone; the unattributed gauge
  over owned and unowned rows. - **Over SQL Server and RabbitMQ**:
  `MessagingRegistrationTests`, the broker-binding test, and an order's eight
  events sent to `bff-order-events` producing the row §10.7 describes — sent to
  the queue rather than published, as Notifications' suite does, because
  `bff-svc` writes no contract exchange and widening the test's grant would undo
  what the binding test proves. - **Through the host**: both routes for the
  owner, another buyer and an unowned row; the clamp; a page boundary between
  two orders with one `FirstSeenAt`; a null `productName`; an owned row with no
  lines; no outbound call during either route; the readiness set is exactly
  `sql` and `masstransit-bus`. - **The rebuild**, in PR-4: rows staged in four
  publisher schemas, a `--reset` run producing the projection the consumers
  produced from the same events, and a type outside the eight never sent.

## 12. The chapters that move, by PR

**ADR-051's table is the owner of the list**, and every row goes to the PR
whose change makes its sentence false. **The rule for each is to name the
set or cite ADR-051, never to write a count**.

| ADR-051's row | Taken by |
|---|---|
| §2.2 — the BFF as aggregation only, one outbound edge | 2: the broker edge, the database and the label |
| §3.2 — no BFF row in the subscription table | 2 |
| §4.1 — one project under `BFF/`, no migrator | 1 |
| §4.2 — the argument for the readiness exemption | 1 |
| §12.1 — one BFF suite, no level for a consumer or a schema | 2: the row names the levels the suite gains in PR-1 and PR-2 |
| §13.5 — the BFF as one of two hosts whose dependencies do not gate readiness | 1 |
| §14.1 — no connection string and no broker credential | 1 for SQL and the migrator; 2 for the broker |
| §14.2 — an AppHost resource referencing Catalog alone | 1 for the database and the migrator; 2 for the broker |
| §15.2 — "two of the fourteen", twelve images | 1 |
| ADR-036 — no broker account for the BFF | 2: a callout naming `bff-svc`, ADR-045's form |
| `Web.Bff.csproj` — "consumes no Ordering message type and must not start" | 1 for its "one project" comment; 2 for the sentence |

**The places outside that table.**

- **§9.5** gains the sentence that a service with no command pipeline
  purges no markers, in PR-1.
- **§10.7** gains the owned-row-with-no-lines sentence in PR-3 and the
  rebuild's window in PR-4, and **ADR-051** the window's callout in PR-4.
- **§4.1's tree and its sentences on which trees hold source,
  `docs/repo-map.md` and `CLAUDE.md`'s tree** each gain `tools/bff-replay/`,
  in PR-4.
- **§15.3** gains the BFF among the charts that render a migration job, and
  **`docs/repo-map.md`**'s BFF entry stops calling it the gateway's shape,
  in PR-1.
- **§13.6** gains the rule's row in its alert table and **§13.9** the
  runbook's row in its table, and `docs/runbooks/README.md` its index row,
  in PR-5: `check.py` refuses a runbook neither table names.
- **§6.6's callout is already true** and does not move. **§10.1**, which
  calls the BFF aggregation, is re-read in PR-3 and moved only if a
  sentence there says it holds no state.
- **Appendix B gains no row**: every package the new projects reference is
  pinned and listed today.
- **Appendix C gains no row**.

`/validate-blueprint` runs on every PR above that edits a chapter.

## 13. What this design deliberately does not do

- **No synchronous call and no Shipping endpoint.** ADR-051.
- **No tracking feed.** §10.7's callout.
- **No new permission.** Section 1.
- **No purge of order history.** Section 1.
- **No erasure consumer.** Section 1 names the path; §11.7's extension
  brings it.
- **No server-sent stream.** Issue #475 waits on this read and is its own
  pull request.
- **No admin read.** The admin repository's journey screen calls this route
  as a buyer; an operator's view of somebody else's order is Ordering's or
  Payments' admin surface, and is not added here.
