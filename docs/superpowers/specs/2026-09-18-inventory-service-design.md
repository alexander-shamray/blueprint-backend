# Inventory — the third service

Design spec, frozen at write time. No PR number: Appendix C is closed at
PR-37 and says a gap the plan left is a pull request whose body says so, so
this is dated and named for its subject, like the checkout-quote spec before
it. Where this document and the blueprint disagree, the blueprint wins.

**What is already decided, and where.**
[§3.2](../../backend-architecture/03-bounded-contexts.md) gives Inventory its
row: it owns `StockItem` and `Reservation`, publishes `StockReserved`,
`StockReservationFailed`, `StockReleased` and `StockLevelChanged`, consumes
`OrderCancelled` and `ShipmentDispatched`, and accepts `ReserveStock` and
`ReleaseStock`. All six contracts exist in `Common.Contracts.Inventory.V1` since
PR-15, with samples in `Platform.IntegrationTests`.
[§7.3](../../backend-architecture/07-persistence.md) prints the reservation
write as one atomic `UPDATE … WHERE Available >= @Quantity` and names Inventory
the exception to optimistic concurrency.
[ADR-024](../../backend-architecture/adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)
owes the service two guarantees on `ReleaseStock`, and
[ADR-029](../../backend-architecture/adr/ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)
keeps `OrderCancelled` in its Consumes column and leaves one gap open by name.
[§9.5](../../backend-architecture/09-messaging.md) says `inventory-commands` is
declared the way `ordering-commands` is. And four things are already waiting on
it: the gateway's `inventory-admin` route and its `inventory:admin` policy
([§10.2](../../backend-architecture/10-api-gateway.md)), the realm's permission
of the same name, `inventory-commands` in the broker's permission patterns, and
[`order-review.md`](../../runbooks/order-review.md), whose procedure checks
and releases a reservation "through Inventory's own API so its invariants
run".

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. Four questions the blueprint leaves open, and their answers

**What a `StockItem` is.** One row per `ProductId`, holding `Available` and
`Reserved` — exactly §7.3's columns. No locations and no SKU distinct from
the product. `CLAUDE.md` names the domain question as undecided, and this is
where it was raised: the e-commerce domain is taken as the specification it
reads as, because §7.3's SQL keys on `ProductId` alone and a location would
amend that fence and §3.2's ownership cell together.

**What consuming `ShipmentDispatched` does.** §3.2 lists it and no chapter
says what it means. It fulfils the reservation: the stock has left the
building, so each line's `Reserved` falls by its quantity and `Available`
stays where the reserve put it. Nothing is published, because no event in
§3.2's Publishes column describes despatch and Catalog's level did not move.
The sentence stating this derivation joins §3.2 beside the `OrderCancelled`
bullet, in PR-2, because the chapter itself says a pair of cells does not
state a derivation and a reader has been joining them by hand.

**Whether to close ADR-029's gap.** No. The ADR says the decision to decline
a release for a confirmed order wants a real picking process behind it, and
there is none. Inventory is built to §3.2 as written: no `OrderConfirmed`
subscription, every cancellation releases, and the runbook's manual
reinstatement stays the procedure. What this costs is stated in section 5 below.

**Where stock comes from.** The admin API only. Nothing seeds
([§14.3](../../backend-architecture/14-local-development.md) is
specification-only) and Inventory consumes no Catalog event, so a
`ReserveStock` naming a product with no row is answered as out of stock —
which is true, and needs neither a seed nor a subscription §3.2 does not
give.

## 2. The PR sequence

Ordering was built as three PRs — scaffold, consumers, saga — and the same
shape fits. Five here, because Catalog's consumer is the other half of one
event and the deploy tree is a class of its own. Each row names its
[change class](../../change-locality.md); the touch set is the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(inventory): third service from the scaffold` — the scaffold run, `StockItem` and its two admin endpoints, the Compose pair, the gateway's `depends_on`, `ci.yml`'s filter and image matrix, the realm's grant to `demo` and the building-block test that pins it, and the three sentences that say Inventory answers 502 | A+B+D |
| 2 | `feat(inventory): reservations` — `Reservation`, `inventory-commands` and the broker grant that lets it be declared, ADR-024's guarantees, the four events, the three reservation admin endpoints, §3.2's despatch sentence | A+B+D |
| 3 | `feat(inventory): consume OrderCancelled and ShipmentDispatched` — `inventory-events` and its two handlers, and the one `AddMeter` line in `Common.Web` that lets section 13's counter be exported | A+B |
| 4 | `feat(deploy): Inventory's chart, deploy target and canary` — `deploy/helm/inventory`, the umbrella dependency, `smoke.sh`'s lists, `deploy.yml`'s option, the canary preflight | D |
| 5 | `feat(catalog): consume StockLevelChanged` — Catalog's binding and the broker grant it needs, the level's projection and its column on the listing, the scaffold's patches for a template that now consumes, and the cut of the test that says Inventory does not exist | A+D |

**Why CI joins PR-1 and Helm does not.** The pipeline gate refuses an
immediate child of `src/Services/` no path filter matches and a Dockerfile
no matrix entry builds, so the scaffold's output cannot land without
`ci.yml` moving in the same PR. `smoke.sh` checks its chart list against the
charts on disk, in both directions, so a chart is owed the day it is added
and not before. §15.3's own sentence — a number describing the tree is wrong
on the PR that adds Inventory; the rule is not — is what PR-4 verifies.

**Why PR-1 is not the whole service.** It is the scaffold's second dogfood
and the smallest tree that proves the pair boots, migrates and answers a
token. PR-18 took the same first step for Ordering, and the argument is the
same: a reservation model reviewed beside the scaffold's generated suite is one
nobody reads.

**Order.** 1 → 2 → 3, then 4 and 5 in either order. Nothing in 4 depends on
3, but a deployed Inventory that consumes no events is a service that holds
every reservation until a person notices, so 4 waits.

## 3. Domain

Two aggregates. One table is written two ways, and the argument for that is
§7.3's own.

**`StockItem`**, keyed by `ProductId`, holds `Available`, `Reserved` and
`UpdatedAt`. It is the aggregate for the admin path only. `SetOnHand(int
onHand, DateTimeOffset now)` sets `Available = onHand - Reserved`, refuses
with a domain error when `Reserved > onHand` — a stock-take cannot make the
warehouse hold less than it has promised — and raises
`StockLevelChangedDomainEvent(ProductId, Available, now)`. It is EF-mapped
with a `rowversion`, so an admin write that races a reservation gets §7.3's
`409 Conflict` from `ConcurrencyExceptionHandler` and the operator retries.
That is the default §7.3 states, applied to the one path that is not
contended.

The same row's counters are also written by the reservation path's raw
statements, and `StockItem` is never loaded on that path. Two write paths to
one table is the exception §7.3 prints, and the rule those statements follow
is `IUnitOfWork.ExecuteRawAsync`'s — raw SQL on the transaction's own
connection, for a table with no aggregate behind it, refused when no
transaction is open. They do not go through that member, because the
printed statement returns the level it left and that member returns
nothing; they go through a ledger port of their own, `IStockLedger`, whose
Infrastructure half reads the unit of work's current transaction off the
`DbContext` and refuses to run without one, the same refusal in the same
place.

**`Reservation`**, keyed by `OrderId`, has a `Status` of `Reserved`,
`Failed`, `Released` or `Fulfilled`, and a list of `ReservationLine(ProductId,
Quantity)` that is kept in every state. It is the one modified aggregate root
in every message-driven command, which is how §6.3's one-transaction,
one-aggregate check holds while `StockItems` moves underneath it: raw SQL is
not tracked, so `ModifiedAggregateCount` is one.

It raises the domain events the outbox mapper turns into §3.2's four
contracts — `StockReservedDomainEvent`, `StockReservationFailedDomainEvent`
with the unavailable product ids, `StockReleasedDomainEvent` — and one
`StockLevelChangedDomainEvent` per line whose `Available` moved, carrying
the level the statement's `OUTPUT inserted.Available` returned. A
product-level event raised by an order-keyed aggregate is the compromise
this design makes on purpose: §7.5's collector reads events off tracked
aggregates, the reservation is the only one tracked on that path, and it is
the thing that moved the level.

**The tombstone is a row in this table.** A release for an order Inventory
has never seen creates a `Reservation` in `Released` with no lines. That is
ADR-024's second guarantee as data rather than as a flag: the reserve that
follows finds a `Released` row and is refused.

Typed identifiers follow §5.2 and Ordering's `OrderId`: `ProductId` and
`OrderId` as `readonly record struct`s of a `Guid`, in `Inventory.Domain`.

## 4. The reservation write

§7.3 fixes the per-row statement and not how a multi-line reserve is
all-or-nothing while still publishing `StockReservationFailed`. The event
has to commit, and `TransactionBehavior` rolls back everything on a failed
`Result` and stages no outbox row for it — so a failed reservation is a
**successful** command that commits a `Failed` row, an outbox row, and no
change to stock.

**A T-SQL savepoint, on the transaction's own connection.** The handler,
inside the unit of work, through the ledger port section 3 names:

1. Loads the `Reservation` for the order. The table at the end of this
   section says what an existing one means.
2. Issues `SAVE TRANSACTION Reserve`.
3. For each line **in `ProductId` order**, runs §7.3's statement exactly as
   printed, with its `OUTPUT inserted.Available`, and records either the
   returned level or the product id of a line that affected no row. Every
   line runs, so the unavailable list is complete rather than the first
   shortfall.
4. If any line failed, `ROLLBACK TRANSACTION Reserve`. Every decrement is
   undone atomically; the transaction stays open; the `Reservation` is
   committed as `Failed` with its lines and raises the failure event.
5. Otherwise the `Reservation` is committed as `Reserved` and raises
   `StockReserved` and one level event per line.

`ProductId` order is what keeps two reservations for overlapping products
from deadlocking: both take their row locks in the same sequence. The
savepoint and rollback are plain statements on the transaction's own
connection, so nothing in `IUnitOfWork` changes — and the execution
strategy's retry restarts the whole unit from a fresh transaction, so a
transient fault mid-sequence cannot leave a savepoint dangling.

**Rejected.** Inverse updates on the first shortfall are correct and need no
savepoint, but report one failing line unless a separate read follows and
put two statement shapes in front of a reviewer. Lock-then-check with
`UPDLOCK, HOLDLOCK` is the textbook shape and is not the fence §7.3 prints;
it holds locks across a round trip for nothing the savepoint does not buy.

**A malformed command is refused before the savepoint, and not retried.**
The `ReserveStock` mapper throws `ContractMappingException` — which the
endpoint's retry policy excludes, per §9.5 — for a line whose quantity is
below `OrderLimits.MinQuantity`, for a product repeated across lines, and
for no lines at all. The bounds are Ordering's contract constants rather than
Inventory's own, because the saga builds the command from an order that
already satisfied them, so a violation here is a bug in the sender and not a
stock decision. No ceiling on line count is declared here for the same
reason: `OrderLimits.MaxLines` bounds the order the lines came from.

**Outcomes of `ReserveStock`, by the reservation's existing state:**

| Found | Does | Publishes |
|---|---|---|
| nothing | the sequence above | `StockReserved` + levels, or `StockReservationFailed` |
| `Released` | refuses; moves no stock | `StockReleased` — ADR-024's second guarantee, and the ADR's own argument for why not `StockReservationFailed` |
| `Reserved` | nothing | `StockReserved` again |
| `Failed` | nothing | `StockReservationFailed` again, with the recorded ids |
| `Fulfilled` | nothing | `StockReserved` — it was, and the stock has since shipped |

The inbox filter already drops a redelivery of the same message id. The
three re-answers are for a command that arrives under a fresh id — a
sender's retry after a lost acknowledgement — and the rule is ADR-024's:
the saga's only alternative to an answer is a pager, so silence is never the
outcome.

## 5. Release, cancellation and despatch

**`ReleaseStock` and `OrderCancelled` are one command with two arrivals.**
`ReleaseStockCommand(OrderId, CommandOrigin)` is dispatched by the
`inventory-commands` consumer, by the `OrderCancelled` handler, and by the
admin endpoint with `CommandOrigin.User`. The two origins are the two §9.5
allows, both literals, and the handler does not read one off a message.

| Found | Does | Publishes |
|---|---|---|
| `Reserved` | returns each line to `Available` by a statement with no guard, in `ProductId` order; status `Released` | `StockReleased` + one level per line |
| `Failed`, `Released`, `Fulfilled` | nothing — nothing is held | `StockReleased` |
| nothing | creates the tombstone | `StockReleased` |

Every row publishes: that is ADR-024's first guarantee, and it is a property
of this table rather than of any one branch.

**`ShipmentDispatched` dispatches `FulfilReservationCommand(OrderId)`.**

| Found | Does | Publishes |
|---|---|---|
| `Reserved` | for each line, `Reserved = Reserved - @Quantity WHERE … AND Reserved >= @Quantity`; status `Fulfilled` | nothing |
| `Fulfilled` | nothing | nothing |
| `Released` | nothing to stock; records `DespatchedUnreservedAt` on the row, which section 13 counts and which refuses a later reinstate, since the parcel has gone | nothing |
| `Failed`, nothing | logs at warning; acks | nothing |

**The `Released` row at despatch is ADR-029's open gap, met in data.** The
stock was returned to `Available` on the cancellation and has now physically
left, so the level is wrong by the recorded lines. This design moves nothing
there, on purpose: the runbook's `cancelled_after_confirmation` procedure has
an operator reinstate the reservation while the parcel is still in the
warehouse, and a `Released` row reaching despatch means that step did not
happen. Subtracting the lines here would make `Available` negative and hand
Catalog a level below zero; leaving it makes the count a stock-take matter,
which is what the metric and the log line are for. Closing the gap properly
needs `OrderConfirmed`, which is the decision section 1 declines, and the
metric is what says how often the decision costs anything.

## 6. The admin API

Under `/v1/inventory`, which the gateway's `inventory-admin` route already
reaches with `/api` stripped. Every endpoint requires `inventory:admin`,
re-validated in the service by an `InventoryPermissions.Admin` constant and a
`RequirePermission` policy, the way Catalog validates `catalog:write` —
§11.3's per-service re-validation, and the gateway's policy is not a reason
to skip it.

| Endpoint | Command or query | Answers |
|---|---|---|
| `PUT stock/{productId}` with `{ onHand }` | `SetOnHandCommand` — upserts the `StockItem` | `204`; `422` when reserved exceeds on-hand, a domain rule; `409` only on a stale rowversion, which is §10.5's concurrency row and not this service's to map |
| `GET stock/{productId}` | `GetStockQuery`, Dapper over the write table | `{ available, reserved, updatedAt }`; `404` |
| `GET reservations/{orderId}` | `GetReservationQuery` | status and lines; `404` — the runbook's step one |
| `POST reservations/{orderId}/release` | `ReleaseStockCommand` with `CommandOrigin.User` | `204` always, because the command always establishes its postcondition — the runbook's step two |
| `POST reservations/{orderId}/reinstate` | `ReinstateReservationCommand` | `204`; `422` `reservation.not_reinstatable` when the row is not `Released`, has no lines, or has already met a despatch (section 5) — one code, because all three mean there is nothing to restore and the description says which; `422` `reservation.unavailable` naming the unavailable ids when stock is short. No `409`: §10.5 reserves it for concurrency and idempotency, and `ErrorType` stays at its three members |

**Reinstate is the runbook's promise, kept.** `order-review.md` already says
an operator reinstates a picked reservation by hand, and ADR-024's tombstone
means a second `ReserveStock` cannot be that mechanism — it would be refused.
So reinstatement is its own command: it re-runs section 4's sequence for a
`Released` row's recorded lines and, on success, moves the row back to
`Reserved` and publishes the levels **and not `StockReserved`**. It answers
an operator, not a saga; the saga that would have consumed `StockReserved`
finalised long ago, and Ordering's `StockReservedHandler` would only reject
a `ConfirmStock` on an order that is already confirmed.

`PUT` is idempotent by shape and the two `POST`s by postcondition, so none
opts into §8.5's key. A tombstone with no lines is refused by reinstate with
`422`, because there is nothing recorded to restore.

## 7. Persistence

Schema `inventory`, database `Inventory`, both connection keys, as the
scaffold names them. Three write-model tables from the EF model, beside the
scaffold's outbox, inbox and idempotency-marker tables:

| Table | Key | Columns |
|---|---|---|
| `StockItems` | `ProductId` | `Available int`, `Reserved int`, `UpdatedAt datetimeoffset`, `RowVersion rowversion` |
| `Reservations` | `OrderId` | `Status`, `UnavailableProductIds nvarchar(max)` — the ids a failed reserve named, kept so the answer can be repeated (section 4); `CreatedAt`, `UpdatedAt`, `DespatchedUnreservedAt datetimeoffset NULL` and `UnreservedCounted bit` — section 13's fact and its claim; `RowVersion rowversion` |
| `ReservationLines` | `(OrderId, ProductId)` | `Quantity int` |

`Status` is stored as a string, §7.2's convention for an enum whose members
a reader of the database should be able to name. `Reservations` takes a
rowversion because the same order's release and fulfilment can arrive on two
endpoints at once, and the loser of that race must retry against the
winner's row rather than overwrite it; `StockItems` takes one for section
3's admin path and nothing else.

Two migrations after the scaffold's own: `AddStockItems` in PR-1 and
`AddReservations` in PR-2, each emitted by `dotnet ef migrations add` from
the configuration that owns the table, so the DDL is the model's and not a
second copy of it.

**A release and a fulfilment for the same order can arrive on two endpoints
at once**, and the rowversion is what settles it: the loser throws
`DbUpdateConcurrencyException` out of the consumer, MassTransit retries it
under the endpoint's policy, and the retry loads the winner's row and takes
the branch that row now calls for. That is a fault time fixes, which is
§9.8's definition of what retry is for.

**Reservations are not purged in this sequence.** ADR-024 bounds the
tombstone's life by the order's, and nothing in the platform states what an
order's lifetime is — `DespatchTimeoutDelay` is days and a review row can
outlive it. Reaping on any figure derived from a ladder is the mistake the
ADR records, so the row per order is kept, which is the cheap side of that
uncertainty. PR-2's body files the purge as an issue naming the bound it
would need.

## 8. Messaging

`inventory-commands` is declared in `Inventory.Infrastructure.Messaging`'s
`AddMassTransitMessaging` the way §9.5 prints `ordering-commands`: the
inbox filter outside the in-memory outbox, `RetryPolicy.Standard` with
`ContractMappingException` excluded, and one `CommandConsumer<,>` per
command in the Accepts column — `ReserveStock` to `ReserveStockCommand`,
`ReleaseStock` to `ReleaseStockCommand` with `CommandOrigin.System`.

One event queue, `inventory-events`, binds `OrderCancelled` and
`ShipmentDispatched`. Ordering splits its event queues because its two
consumers speak two failure vocabularies; both of these dispatch a command
whose rejections are acked, so one queue and one retry policy is the honest
count. A `MessagingRegistrationTests` in `Inventory.Api.Tests` asserts that
every cell of §3.2's Consumes and Accepts columns has both an `AddConsumer`
and a `ConfigureConsumer` — the test §9.5's warning about a handler with no
line implies.

`InventoryIntegrationEventMapper`'s registry is §3.2's Publishes column and
exactly it: four entries. The scaffold adds the `inventory-svc` broker
account with the per-service permission patterns #44 established, and the
secret-scan allow-list entries its rendered literals need.

## 9. Testing

By [§12](../../backend-architecture/12-test-strategy.md)'s layers, and the
container tests are `Category=Integration` and never skipped.

- **`Inventory.Domain.Tests`**: every `Reservation` transition and refusal;
  `StockItem.SetOnHand`'s arithmetic and its one refusal; the events each
  raises.
- **`Inventory.Application.Tests`**: the command mappers, including
  `ReserveStock`'s lines to `ReservationLine`s; the validators; the outbox
  mapper's registry against the four contracts; the architecture tests.
- **`Inventory.Api.Tests`**, over SQL Server and RabbitMQ containers:
  - **the test that justifies §7.3's exception** — two concurrent reserves
    for the last unit, where exactly one row is affected and exactly one
    `StockReserved` is staged;
  - the savepoint: a three-line reserve with the middle line short commits a
    `Failed` row naming that one product and leaves all three counts as they
    were;
  - the tombstone: release then reserve publishes two `StockReleased` and
    holds nothing;
  - every row of section 5's release table publishes;
  - fulfilment moves `Reserved` and not `Available`, and a `Released` row
    at despatch moves nothing and counts;
  - reinstate restores a `Released` row's lines and publishes no
    `StockReserved`;
  - `EndpointSecurityTests`, `AuthorizationPolicyTests` and
    `GrantablePermissionTests` in Ordering's shape, over `inventory:admin`;
  - `MessagingRegistrationTests` as section 8 states it;
  - the counter's claim: two `Local` deliveries of the same unreserved
    despatch increment `inventory.fulfilment.unreserved` once.
- **`Catalog.Api.Tests`**, in PR-5: an older level arriving after a newer
  one leaves the newer row; the same level twice leaves one row; a product
  never reported lists `null`; and the registration test gains the cell
  its comment said was waiting.
- **`Platform.IntegrationTests`** already samples the six contracts; nothing
  new is owed there.
- **The scaffold's own suite** is unchanged; PR-1's body carries the dogfood
  evidence the way PR-18's did.

## 10. The chapters that move, and the ones that do not

- **§3.2** gains one sentence beside the `OrderCancelled` bullet, in PR-2:
  consuming `ShipmentDispatched` fulfils the reservation and publishes
  nothing. The one derivation the row leaves to the reader.
- **§10.2, §14.1 and `deploy/compose/README.md`** each say Inventory's
  route answers 502 until the service lands; PR-1 lands it and moves each
  sentence to the past it describes. §14.1's own paragraph predicts this
  divergence by name.
- **Catalog's `MessagingRegistrationTests`** says Inventory does not exist
  and that §8.4's invalidator needs a cached query; PR-5 cuts the comment,
  binds the consumer, and does what section 14 says with the level.
- **The realm's `inventory:admin` description** says the route it guards
  has no service behind it; PR-1 rewrites it to say what the role now
  grants, per section 11.
- **ADR-024 and ADR-029 do not move.** Both say nothing enforces them until
  Inventory is built. An ADR is superseded and never rewritten, and those
  paragraphs are the history the tests in section 9 now make true.
- **Appendix C gains no row.** §4.1's tree already names Inventory as "the
  same five projects", which stays true.

## 11. Local development and the realm

The scaffold takes a host port and refuses one already published, so the
port is a decision this document takes: **5103**, the gap between Ordering's
5101, Catalog's 5102 and the BFF's 5200 in `deploy/compose/README.md`'s
table, which the scaffold's own edit adds the row to.

**`demo` gains `inventory:admin`.** The realm export grants the permission to
nobody, with a description saying the route has no service behind it. With
stock existing only through the admin API (section 1), a local checkout in
which no shipped login holds the permission is one in which no reservation
can ever succeed, so PR-1 adds the role to `demo`'s `commerce-api` client
roles beside `catalog:write`, `orders:write` and `orders:cancel`, and
rewrites the description to say what it grants. `orders:admin` stays
ungranted, and the difference is §14.1's: that permission overrides §11.4's
ownership check and is demonstrable only while no login can bypass it, where
this one guards a route with no ownership to override. The change to the
export runs `realm.yml`'s gate, which asserts token obligations and is
indifferent to role grants, and `GrantablePermissionTests` in
`Inventory.Api.Tests` asserts the other direction — that every permission an
endpoint requires is a role the realm can grant — in Ordering's shape.

## 12. Configuration and deployment

**PR-1's keys are Ordering's, renamed.** `ConnectionStrings__Inventory`,
`ConnectionStrings__InventoryMigrator`, `ConnectionStrings__RabbitMq` with
the `inventory-svc` account, `Identity__Authority`,
`OTEL_EXPORTER_OTLP_ENDPOINT`, and both Redis keys — both, because the
scaffold copies Catalog's `AddRedisConnections` and §15.4 says a host given
one key throws naming the other. §15.4's table is keyed by key rather than
by host and every row Inventory needs already exists, so the table does not
move; the Compose unit the scaffold writes is where the values live locally.

**PR-4's chart is Ordering's shape with one workload name.** `workload.name`
is `inventory-api`, because §10.2's route file already dials that literal;
`image.api` and `image.migrator` are `inventory-api` and
`inventory-migrator`, matching PR-1's matrix entries; `service.enabled` is
true, since the gateway dials it. It joins `MIGRATOR_CHARTS` in `smoke.sh`,
the umbrella's dependencies, `deploy.yml`'s target list, and
`deploy/canary/canary.json`'s workload map — where `inventory-api-migrate-`
costs 22 characters against the Job name's 63, so the tag budget is 41, one
under Ordering's; the canary derives that rather than reading it here.

## 13. Observability

**What comes free.** `MessagingMetrics` in `Common.Infrastructure` already
counts every delivery lag and every domain rejection per message type, the
outbox dashboard reads per-service outbox depth by resource attribute, and
the golden-signals board keys on the host. None of that names a service, so
`inventory-api` appears on both the day it exports; PR-4 confirms it from a
rendered chart rather than assuming it.

**One business-shaped counter, and it follows §13.3's rule.**
`inventory.fulfilment.unreserved` counts a `ShipmentDispatched` that met a
`Released` reservation — section 5's open-gap case. §13.3 forbids
incrementing inside the write transaction and requires the counter to be a
claim against a row, so the handler records the fact as state,
`DespatchedUnreservedAt` on the `Reservation`, and a `Local`-lane projection
handler claims it — one `UPDATE` that flips a `UnreservedCounted` flag where
it is unset and the timestamp is set — and increments only when a row was
affected. No counters for reserved, failed, released or fulfilled outcomes:
each is already a message the outbox and broker dashboards count, and a
second count of the same fact is the reconciliation §13.3 warns against.

**No new dashboard and no new alert.** The runbook procedure the counter
serves is a review-row one, and §13.6's `Orders awaiting review` alert
already fires on the row `cancelled_after_confirmation` writes. A panel
joins the golden-signals board only if PR-4's render shows the host missing
from it.

## 14. What Catalog does with the level

Catalog's test says §8.4's invalidator needs a cached query to invalidate,
and none exists: no Catalog handler reads `HybridCache`, so an invalidator
would be a handler for a cache that is not there. What Catalog owes the
event instead is a **projection**, which is what §3.2's Consumes cell is
for and what the contract's own remark anticipates — a level, not a delta,
with a watermark on `OccurredAt` because §9.4 orders nothing.

- **`catalog.StockLevels(ProductId, QuantityAvailable, AsOf)`**, a read
  model mapped through an `IEntityTypeConfiguration` on the terms §7.4
  states for `ProductPrices` — `migrations add` emits it, the configuration
  produces the printed types, and nothing reads it through EF — written by
  an `IIntegrationEventHandler<StockLevelChanged>` on a
  `catalog-inventory-events` queue with one statement: upsert where the row
  is absent or `AsOf` is older than the event's `OccurredAt`, and no write
  otherwise. A stale level arriving late changes nothing, and a redelivered
  one changes nothing twice.
- **`GetProducts` gains `quantityAvailable`**, nullable: a product Inventory
  has never reported reads `null` rather than zero, because "unknown" and
  "none" are different facts to a screen and collapsing them is how a new
  product lists as sold out. The listing is served anonymously through the
  gateway's `catalog-public` route and the BFF does not touch it, so
  nothing in the BFF changes; what a screen does with the value is the
  frontend's.
- **The invalidator is not owed until a cached query exists.** The test's
  comment is cut rather than fulfilled, and the first Catalog handler to
  cache a product read brings §8.4's handler with it, beside this
  projection, in the order §8.4 already says the reader decides.

## 15. What this design deliberately does not do

- **No reservation expiry.** §9.6's `StockTimeout` cancels the order, and
  the cancellation releases. A second clock in Inventory would be a second
  opinion on the same wait.
- **No dev seed.** Section 1's answer; a seeder is §14.3's first, and a PR of
  its own.
- **No `OrderConfirmed`.** section 1's answer, and section 5 says what it costs.
- **No location, no SKU, no supplier.** section 1's answer.
- **No `StockLevelChanged` on fulfilment.** The level did not move.
- **No purge of `Reservations`.** Section 7's argument, and an issue.
- **No outcome counters.** Section 13's argument: the messages are already
  counted.
- **No cache in Catalog.** Section 14's argument: a projection is what the
  event is owed, and an invalidator waits for a cached query.
