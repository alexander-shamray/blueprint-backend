# Payments — the fourth service

Design spec, frozen at write time. No PR number: Appendix C is closed at
PR-37 and says a gap the plan left is a pull request whose body says so, so
this is dated and named for its subject, like the Inventory spec beside it.
Where this document and the blueprint disagree, the blueprint wins.

**What is already decided, and where.**
[§3.2](../../backend-architecture/03-bounded-contexts.md) gives Payments its
row: it owns `PaymentIntent` and `Refund`, publishes `PaymentAuthorised`,
`PaymentDeclined` and `PaymentRefunded`, consumes `OrderPlaced` and
`OrderCancelled`, and accepts `AuthorisePayment`. All four of its own
contracts exist in `Common.Contracts.Payments.V1`, and the two it consumes in
`Common.Contracts.Ordering.V1`. The same section says Payments builds its own
record of the order from `OrderPlaced` — the payer, the total and the
currency — and that a missing record is a wait, not a decline, served by
delayed redelivery reaching §9.6's payment timeout.
[ADR-028](../../backend-architecture/adr/ADR-028-a-money-movement-command-carries-no-subject.md)
keeps the payer off `AuthorisePayment` and lets Payments refuse a mismatch of
amount or currency. [§9.6](../../backend-architecture/09-messaging.md) states
that Payments voids off `OrderCancelled`, and
[`order-review.md`](../../runbooks/order-review.md) builds its money procedure
on that void. Three things are already waiting on the service: Ordering's
saga sends to `payments-commands`, the broker's permission patterns name it,
and §4.1's tree names `Payments/` with the same five projects.

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. What the blueprint leaves open, and the answers

**What sits behind the anti-corruption layer.** §3.2 names Payments an ACL
over a PSP and the repository has none. The answer is a **simulator over
HTTP**: a WireMock.Net image with JSON mappings, behind a typed `HttpClient`
adapter. The ACL is then real — a network, a timeout, a retry, a translation
of a foreign vocabulary — and no credential and no external dependency enters
CI or a local checkout. A real provider's sandbox is the same adapter pointed
elsewhere, and a decision for the day one is chosen.

**How far the lifecycle goes.** Authorise and void, and nothing else. §3.2
gives Payments no event that means "the goods have gone", so there is no
trigger for a capture, and adding one is a Consumes cell and an ADR rather
than a detail of this service. An authorisation the simulator gives never
settles. `PaymentRefunded` is published for a void: the contract's own
summary says "refunded" and `OrderCancelled`'s says "voided", and for an
authorisation that was never captured the two are one act.

**What a mismatch is.** An `AuthorisePayment` whose amount or currency
differs from Payments' record is a **fault**, not a verdict: nothing is
charged, nothing is published, the message is excluded from retry and reaches
the error queue §13.6 pages on, and the saga's payment timeout compensates. A
mismatch is a defect in the sender or a forgery; `PaymentDeclined` is §3.2's
verdict about a payer, and publishing it would make a platform defect read as
a customer's card being refused. This is the line Inventory's design draws
for a malformed `ReserveStock`.

**Whether operators get a surface.** A read, and no write. `order-review.md`
step 1 asks whether Payments already refunded; today the only answers are the
broker and the provider's console. `GET /v1/payments/{orderId}` gives it a
first-party one. A manual refund stays at the provider, as the runbook says.

**Where the PSP call sits.** Inside the command's unit of work, made safe by
an idempotency key — section 4.

## 2. Two places the blueprint moves

**§2 says Payments "reaches only the coordination instance: it takes
idempotency keys (§8.5) and caches nothing."** §8.5's keys are claimed by HTTP
write commands, and Payments has none — its one endpoint is a `GET`. Its
idempotency is the PSP's key (section 4) and its own rows. The scaffold copies
Catalog's `AddRedisConnections`, which reads both connection keys, so the
scaffold's output contradicts §2 twice. PR-1 strips the call, so Payments
takes no Redis key at all, and amends §2's sentence in the same PR to say
Payments reaches neither instance and why. §15.4's rows for the two keys say
"both or neither", which already allows the second.

**A cancellation can reach Payments before the `AuthorisePayment` it
precedes.** §9.4 orders nothing between two deliveries — the race ADR-024
closed for Inventory, one service over. Without a guard the late command
charges a cancelled order, and the saga is in `Compensating`, where a
`PaymentAuthorised` escalates to a person. The answer is recorded as
**ADR-047**, in PR-3, with one sentence joining §3.2 beside its Payments
callout:

- **`OrderCancelled` stamps `CancelledAt` on Payments' record of the order**,
  creating the record as a tombstone when `OrderPlaced` has not arrived.
- **An `AuthorisePayment` for a cancelled order calls no provider and
  publishes `PaymentDeclined` with reason `order_cancelled`**, so the saga's
  payment half settles now rather than on its timeout. `PaymentDeclined.Reason`
  is for a human and is never branched on (§9.8), so no contract moves.
- **`PaymentRefunded` is published only when money moved back.** It reports
  an act, where `StockReleased` reports a postcondition; nobody waits on it but
  Notifications, so a cancellation of an order with nothing authorised
  publishes nothing. This is the deliberate difference from ADR-024, and the
  ADR states it.

The ADR is owed rather than the sentence alone because it decides what a
declined verdict may mean, and the saga depends on that meaning.

## 3. The PR sequence

Ordering and Inventory set the shape. Six here, because the anti-corruption
layer is the part §3.2 names the service for and is reviewed alone, and the
operator surface crosses the gateway and the realm. Each row names its
[change class](../../change-locality.md); the touch set is the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(payments): fourth service from the scaffold` — the scaffold run with `AddRedisConnections` stripped and §2's sentence amended; `PaymentOrders` and the `payments-events` queue consuming `OrderPlaced` and `OrderCancelled` into it, with the broker grant a receive endpoint needs; the Compose pair on port 5104; `ci.yml`'s filter and image matrix; the observability gate's outbox exemption | A+D+E — E because the scaffold adds projects to the solution |
| 2 | `feat(payments): the PSP anti-corruption layer and its simulator` — `IPaymentProvider`, the typed `HttpClient` adapter and its resilience handler, the translation, the provider counter and its `AddMeter` line, `deploy/compose/psp-simulator/` and its Compose service, §15.4's two rows. Nothing calls it yet | A+D+E — E for the test project's `WireMock.Net` reference |
| 3 | `feat(payments): authorise` — `PaymentIntent`, `payments-commands` with its delayed redelivery, the mismatch fault, `PaymentAuthorised` and `PaymentDeclined`, ADR-047, §3.2's sentence and the `PaymentDeclined.Reason` remark, and the ladder's cross-service assertion | C+E — C for the ADR, E for `Platform.IntegrationTests`' two references |
| 4 | `feat(payments): void on cancellation` — `Refund`, the void on the `OrderCancelled` consumer, `PaymentRefunded`, Payments' outbox gauges, metrics initialiser and `AddMeter` line, and the deletion of PR-1's exemption | A+D+E |
| 5 | `feat(payments): operators read a payment` — the admin `GET`, `payments:admin` in the service, the gateway's `payments-admin` route and cluster, the realm's permission granted to `demo`, §10.2's route and `order-review.md`'s step 1 | A+D+E — E for the first query's `Dapper` reference |
| 6 | `feat(deploy): Payments' chart, deploy target and canary` — `deploy/helm/payments` with `redis.enabled: false`, the library chart's `paymentProvider` capability, the umbrella dependency, `smoke.sh`'s lists, `deploy.yml`'s option, the canary map, and §15.3's sentence naming the charts with no Redis | D |

**Four of the six need three classes, and the gate admits two.** A service's
arrival spans its code, its projects and its deployment or harness tree, and
`docs/change-locality.md` names at most two classes; `.github/locality-gate`
refuses a third letter. The scaffold's own render already crosses all three,
so this is not a sequencing choice that a different split would avoid.
Payments' PR-1 cannot merge until the contract and the gate admit a service's
arrival, which is a Class D change of its own and is owed first; Inventory's
plans declare the same shape and meet it before Payments does.

**Why the cancellation is recorded in PR-1 and voided in PR-4.** Both
consumers write the same record, and PR-3's guard reads `CancelledAt`. Were
the stamp to land with the void, PR-3 would ship a check with no data behind
it and the window between the two PRs would be exactly the race section 2
closes.

**Why CI joins PR-1 and Helm does not** is the Inventory spec's argument
unchanged: the pipeline gate refuses a service directory no filter matches,
and `smoke.sh` checks its chart list against the charts on disk in both
directions, so a chart is owed the day it is added and not before.

**Order.** 1 → 2 → 3 → 4 → 5 → 6. Nothing here depends on Inventory's code.
Port 5104 assumes Inventory takes 5103, as its spec decides; the scaffold
refuses a published port, so whichever lands second finds out. Until
Inventory lands, the saga stalls at stock locally and no `AuthorisePayment`
reaches Payments end to end, so PR-3's evidence is its container tests.

## 4. Where the provider call sits

Inside the handler, inside `TransactionBehavior`'s unit of work, with an
idempotency key the provider honours: `authorise:{OrderId}` and
`void:{OrderId}`. The whole unit — read, provider call, aggregate, outbox row,
commit — may be retried, by the adapter, by the endpoint's policy or by a
redelivery after a crash, and each retry receives the provider's first answer
for the same key. The money moves once and the event is staged once, because
the second attempt is a replay at the provider and a fresh transaction here.

The cost is a SQL transaction held across one HTTP call. It holds one row per
order, and the only thing that contends for it is the same order's
cancellation (section 6), which is the contention that has to be serialised
anyway.

**Rejected.** A `Pending` intent committed first and a background dispatcher
calling the provider keeps transactions short and costs a second polling loop,
its own gauges and gate exemption, and a new state behind the saga's wait —
machinery for a contention this service does not have. An asynchronous
provider with a webhook is the most realistic and needs an unauthenticated
ingress route, signature verification and a callback through the gateway, for
a simulator.

## 5. Domain

**`PaymentOrder`**, keyed by `OrderId`, is Payments' record of the order: the
shape §3.2 compares to `ordering.ProductPrices`, a local copy of another
service's fact read on the path that decides. `OrderPlaced` fills
`CustomerId`, `TotalAmount`, `Currency` and `PlacedAt`; `OrderCancelled`
stamps `CancelledAt`. Each write is an upsert that fills only its own columns,
so the two events commute and a redelivery writes the same values again. It
is written and read by raw statements on the unit's transaction, following
`IUnitOfWork.ExecuteRawAsync`'s rule for a table with no aggregate behaviour.
They go through a port of their own, `IPaymentOrderStore`, rather than that
member, because the authorise path's read returns the row it locked (section
6) and the member returns nothing; its Infrastructure half reads the unit's
current transaction off the `DbContext` and refuses to run without one, as
Inventory's `IStockLedger` does.

**`PaymentIntent`**, keyed by `OrderId`, is created in a terminal state:
`Authorised` with the provider's reference, or `Declined` with its reason.
There is no `Pending` row, because section 4 puts the provider's answer
inside the unit that creates it. It raises `PaymentAuthorisedDomainEvent` or
`PaymentDeclinedDomainEvent`.

**`Refund`**, keyed by `OrderId`, is created by a void of an authorised
intent, carries the intent's reference, amount and currency, and raises
`PaymentRefundedDomainEvent`.

Each command modifies one aggregate, which is how §6.3's check holds: the
authorise path creates the intent and reads the record; the cancellation path
stamps the record by raw statement, which is not tracked, and creates the
refund when there is money to void.

`OrderId` is a `readonly record struct` of a `Guid` in `Payments.Domain`, per
§5.2. Amounts are `decimal` in the major unit, as every contract carries
them; the adapter alone converts to minor units.

## 6. The two commands

**`AuthorisePayment`**, by what Payments already holds:

| Found | Does | Publishes |
|---|---|---|
| no record, or a record with neither `PlacedAt` nor `CancelledAt` | throws `PaymentOrderNotYetKnownException`; the endpoint's delayed redelivery (section 8) takes it | nothing yet |
| `CancelledAt` set | no provider call; a `Declined` intent with reason `order_cancelled` (ADR-047) | `PaymentDeclined` |
| amount or currency differs from the record | throws `PaymentMismatchException`, excluded from retry; error queue | nothing |
| an intent for the same money | nothing — its verdict is already staged | nothing |
| an intent, and the command's money differs from it | throws `PaymentMismatchException` | nothing |
| otherwise | authorise at the provider under `authorise:{OrderId}`, the payer being the record's `CustomerId`; the intent in the provider's verdict | `PaymentAuthorised` or `PaymentDeclined` |

An existing intent is acknowledged and not answered again. Its verdict was
staged in the outbox in the transaction that created it, so it reaches the saga
whatever happens to this delivery, and a second `PaymentAuthorised` under a
fresh message id is not idempotent downstream: §9.6's saga escalates one
arriving in `Compensating` and has no transition for one after it confirmed. The
inbox drops a redelivery of the same id, and the saga sends `AuthorisePayment`
once per instance through its own outbox, so a resend under a fresh id is not a
path the sender takes. This is where Payments departs from ADR-024:
`StockReleased` reports a postcondition the saga waits on, and the payment
verdict is an act already on its way. A transient provider fault throws out of
the consumer and is retried by §9.8's policy; the key is what makes that safe.

**`OrderCancelled`**, by the intent's state:

| Found | Does | Publishes |
|---|---|---|
| `Authorised`, no refund | stamps `CancelledAt`; voids at the provider under `void:{OrderId}`; creates the `Refund` | `PaymentRefunded` |
| `Authorised`, refund exists | stamps `CancelledAt` | nothing — the refund was published |
| `Declined`, or no intent | stamps `CancelledAt`, creating the tombstone record when absent | nothing — no money was taken |

**The race between the two is closed by a lock on the record.** An
`AuthorisePayment` and an `OrderCancelled` for one order can run at once on two
endpoints. Were authorise to read the record without a lock, it could see no
cancellation, authorise at the provider, and commit after the cancellation
committed having found no intent to void — a charge on a cancelled order, the
hole ADR-047 exists to close. So authorise reads the record `WITH (UPDLOCK,
HOLDLOCK)` for the life of its unit — `HOLDLOCK` because the row may not exist
yet, and a key-range lock is what makes the cancellation's first insert wait as
its update would — and the cancellation's stamp waits behind it; whichever
commits second sees the first's row and takes that row's branch. The lock is
held across the provider call, which is section 4's cost paid in the one place
it buys something.

## 7. Persistence

Schema `payments`, database `Payments`, both connection keys, as the scaffold
names them. Three write-model tables beside the scaffold's outbox, inbox and
marker tables:

| Table | Key | Columns |
|---|---|---|
| `PaymentOrders` | `OrderId` | `CustomerId uniqueidentifier NULL`, `TotalAmount decimal(19,4) NULL`, `Currency char(3) NULL`, `PlacedAt datetimeoffset NULL`, `CancelledAt datetimeoffset NULL` |
| `PaymentIntents` | `OrderId` | `Status`, `Amount decimal(19,4)`, `Currency char(3)`, `Reference NULL`, `DeclineReason NULL`, `CreatedAt`, `RowVersion rowversion` |
| `Refunds` | `OrderId` | `Reference`, `Amount decimal(19,4)`, `Currency char(3)`, `VoidedAt` |

`PaymentOrders`' columns are nullable because either event may arrive first.
Money is `decimal(19,4)`, Ordering's own money precision — its saga's `Total`
and every line price — so any total Ordering can store, Payments can; and a
contract amount at or above that column's ceiling, or with more places than
the provider's minor units, is refused by the mapper as malformed.
`PaymentAmounts` in `Payments.Application` owns those three numbers, and the
three configurations and the mapper read them. What does not follow is a
ceiling at the source: Ordering bounds no order's total when it is placed, so
an order beyond its own column fails first in Ordering's saga, and that is
Ordering's defect to close, not this service's — #222.
`Status` is stored as a string, §7.2's convention. The migrations are named
for their tables and emitted by `dotnet ef migrations add` from the
configuration that owns each: `AddPaymentOrders` in PR-1, `AddPaymentIntents`
in PR-3, `AddRefunds` in PR-4. Rows are not purged in this sequence, for the
reason the Inventory spec gives for its reservations: nothing states an
order's lifetime to bound them by.

## 8. Messaging

**`payments-commands`** is declared in `Payments.Infrastructure.Messaging`'s
`AddMassTransitMessaging` the way §9.5 prints `ordering-commands` — the inbox
filter outside the in-memory outbox, `RetryPolicy.Standard` with the mapping
exceptions and `PaymentMismatchException` excluded, and one
`CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>`. It adds what
§3.2's callout requires and no other endpoint has: **delayed redelivery**
through ADR-021's exchange, scoped to `PaymentOrderNotYetKnownException` and
nothing else, at 30 s, 1, 2, 4 and 8 minutes. The ladder's sum must reach
`OrderFulfilmentSaga.PaymentTimeoutDelay`, and that is asserted in
`Platform.IntegrationTests`, which gains references to both Infrastructure
projects for it: §4.2 forbids Payments to reference Ordering, and the one
suite that exists to hold two services to each other is where a coupling
between them is pinned. A literal in Payments restating the delay would be
the second owner the locality contract forbids.

**`payments-events`** is one queue binding `OrderPlaced` and `OrderCancelled`.
Both write the record, and a cancellation's void rejects nothing a second
vocabulary would describe, so one queue and one retry policy is the honest
count.

`PaymentsIntegrationEventMapper`'s registry is §3.2's Publishes column and
exactly it: three entries. `MessagingRegistrationTests` in `Payments.Api.Tests`
asserts that every cell of the Consumes and Accepts columns has both an
`AddConsumer` and a `ConfigureConsumer`. The scaffold adds the `payments-svc`
broker account with Catalog's publisher-only patterns, which admit no queue.
`payments-events` is a receive endpoint from PR-1, so PR-1 widens the entry to
`ordering-svc`'s shape with a `payments-` prefix, and that prefix admits
`payments-commands` too when PR-3 declares it. Ordering's existing `write` on
the queue is untouched.

## 9. The anti-corruption layer

**The port** is `Payments.Application`'s, in the domain's vocabulary:
`IPaymentProvider.AuthoriseAsync` returns `Authorised(reference)` or
`Declined(reason)`, and `VoidAsync` returns nothing. A transient fault is not
a result: it throws `PaymentProviderUnavailableException`, which the
consumer's retry owns. Keeping it out of the result type is what stops "the
provider is down" ever reaching the saga as a decline.

**The adapter** in `Payments.Infrastructure.Provider` is the one place that
knows the wire format: `POST /v1/authorisations` with an `Idempotency-Key`
header, the amount in minor units, the currency and the payer's id; `POST
/v1/authorisations/{reference}/void` under the void key. It translates:

| Provider answers | Port returns |
|---|---|
| 201 `approved` | `Authorised(reference)` |
| 402 `declined` | `Declined(code)` |
| 409 — the key reused with different figures | throws `PaymentMismatchException` |
| 408, 429, 5xx, a timeout, a refused connection | throws `PaymentProviderUnavailableException` |

Resilience is `Microsoft.Extensions.Http.Resilience`'s standard handler,
already pinned: a five-second attempt timeout, two retries, a twenty-second
total. Retrying inside the client is safe only because every request carries
its key; the endpoint's policy owns every retry after that, and the worst case
stays inside the saga's payment wait. Configuration is
`PaymentProvider__BaseUrl` and `PaymentProvider__ApiKey`; the simulator
ignores the key, so the Compose unit's value is §14.1's stated
local-development exception, and §15.4 gains both rows with the key as a
secret.

**The simulator** is `deploy/compose/psp-simulator/`: the WireMock.Net image
at the tag `Directory.Packages.props` pins the package to, `mappings/*.json`
and a README. WireMock.Net rather than Java WireMock because §12's table names
it for a third-party API and Appendix B already carries it, and because the
two read different mapping formats: with WireMock.Net on both sides, Compose's
container and the tests' in-process server load the same files. It builds
nothing, so no matrix entry is owed. The verdict is scripted by the amount's
minor units:

| Minor units | Answer |
|---|---|
| `.01` | 402 `card_declined` |
| `.02` | 402 `insufficient_funds` |
| `.05` | 503 |
| `.09` | a thirty-second delay, past the adapter's total |
| any other | 201 `approved` |
| a void | 200 |

The reference is `psp_` followed by the idempotency key, templated from the
request, so the same key always answers the same reference and the simulator
is idempotent while holding no state. The 409 row of the translation table is
the adapter's and the simulator never answers it: a stateless stub cannot know
a key was used before, so the adapter's test stubs that answer for itself. A
plain Compose checkout authorises everything but those amounts, which are how a
person watches a decline, a retry and the saga's timeout. The tests start
WireMock.Net in process over the same `mappings` directory, so one set of files
is what both read.

## 10. The admin read

`GET /v1/payments/{orderId}` → `GetPaymentQuery`, Dapper over the three
tables, answering the record's `placedAt` and `cancelledAt`, the intent's
status, reference, amount, currency, decline reason and `createdAt` or `null`,
and the refund's reference and `voidedAt` or `null`; `404` when no record
exists. It does not answer `CustomerId`: an operator needs the money's state,
not its subject.

It requires `payments:admin`, re-validated in the service by a
`PaymentsPermissions.Admin` constant and a `RequirePermission` policy —
§11.3's per-service re-validation. The gateway gains a `payments-admin` route
under `/api/v1/payments/{**catch-all}` with the same policy and a `payments`
cluster dialling `payments-api:8080`, and §10.2 gains the route. The realm
gains `payments:admin`, granted to `demo` beside `inventory:admin`; the
argument for granting it is the one the Inventory spec makes, since the
permission guards a route with no ownership check to override, which is what
keeps `orders:admin` ungranted. `order-review.md`'s step 1 cites the endpoint
beside the provider's console and nothing else in the runbook moves.

## 11. Configuration and deployment

**PR-1's keys are Ordering's, renamed, less Redis.**
`ConnectionStrings__Payments`, `ConnectionStrings__PaymentsMigrator`,
`ConnectionStrings__RabbitMq` with the `payments-svc` account,
`Identity__Authority` and `OTEL_EXPORTER_OTLP_ENDPOINT`. PR-2 adds the two
provider keys. Port 5104 joins `deploy/compose/README.md`'s table by the
scaffold's own edit.

**PR-6's chart is Ordering's shape.** `workload.name` is `payments-api`;
`image.api` and `image.migrator` match PR-1's matrix entries; `service.enabled`
is true, because PR-5's route dials it. It declares `redis.enabled: false`,
written down rather than omitted as §15.3 says the gateway and the BFF do, and
that sentence gains Payments. The library chart has no generic secret variable —
each capability is a block whose settings it guards — so the two provider keys
arrive as a `paymentProvider` capability on the `identity.clientCredentials`
pattern: the base address in the ConfigMap and the key from a Secret reference,
both required when the block is enabled. A deploy that forgot the address
therefore fails at render, which is the library chart's own rule — a clean
render followed by a pod that cannot start is the shape its guards exist to
refuse — and stricter than the host's refusal at start. The simulator is never
charted. It joins `MIGRATOR_CHARTS` in `smoke.sh`, the umbrella's dependencies,
`deploy.yml`'s target list and `deploy/canary/canary.json`'s workload map.

## 12. Observability

**The outbox gauges** follow the Inventory spec's path: PR-1 adds Payments to
`deploy/observability/check.py`'s exemption with the template's reason, and
PR-4 takes Ordering's `OutboxMetrics`, `OutboxStats` and
`MetricsInitialiser` under Payments' namespace, names `Payments.Outbox` in
§13.2's export and deletes the exemption.

**One counter, `payments.provider.unavailable`,** on a `Payments.Provider`
meter, incremented in the adapter on each attempt that ends in a transient
fault. It is a fact about the provider rather than about an order, so §13.3's
claim rule does not reach it: a unit that rolls back still met a failing
provider. §13.2's export names meters one by one, so PR-2 adds the
`AddMeter` line in `Common.Web` and PR-4 adds `Payments.Outbox`'s. The error
queue sees only
the units that exhausted their retries, and a provider that is failing
half its calls is visible here first. The HTTP client's own meter already
records duration and status per host, so nothing else is hand-written; no
outcome counters, because each outcome is a message the outbox and broker
dashboards already count.

**No new dashboard and no new alert.** A mismatch reaches the error queue
§13.6 already pages on, and the golden-signals board keys on the host. The
`Information` line at an authorisation is §13.4's own example; neither it nor
any other line logs the payer's id or a provider's body.

## 13. Testing

By [§12](../../backend-architecture/12-test-strategy.md)'s layers, and the
container tests are `Category=Integration` and never skipped.

- **`Payments.Domain.Tests`**: the intent's two terminal states and the
  events each raises; the refund's creation and its event.
- **`Payments.Application.Tests`**: every row of section 6's two tables
  against a fake provider; the mapper registry against the three contracts;
  the command mapper; the architecture tests.
- **`Payments.Api.Tests`**, over SQL Server and RabbitMQ containers and an
  in-process WireMock.Net server:
  - the adapter against every row of section 9's simulator table;
  - **the race**: an authorise and a cancellation for one order started
    together end in a `Declined` `order_cancelled` intent, or in an
    `Authorised` intent with a refund — never an authorisation with none;
  - an `AuthorisePayment` before its `OrderPlaced` is redelivered and
    authorises once the event lands;
  - a mismatch reaches the error queue with no request in WireMock's
    journal;
  - a unit retried after the provider answered authorises once at the
    provider and stages one `PaymentAuthorised`;
  - the two record writes commute;
  - `EndpointSecurityTests`, `AuthorizationPolicyTests` and
    `GrantablePermissionTests` in Ordering's shape, over `payments:admin`;
  - `MessagingRegistrationTests` as section 8 states it.
- **`Platform.IntegrationTests`** gains the ladder assertion of section 8;
  its contract samples already cover every contract Payments touches.

## 14. The chapters that move, and the ones that do not

- **§2** says Payments reaches neither Redis instance, in PR-1.
- **§3.2** gains one sentence beside its Payments callout, in PR-3: a
  cancellation recorded before the authorisation answers `PaymentDeclined`
  `order_cancelled`, and a void publishes only when money moved.
- **ADR-047** and its Appendix A row, in PR-3.
- **`PaymentDeclined.Reason`'s remark** in `Common.Contracts.Payments.V1`,
  which says the reason is the provider's, gains ADR-047's one reason of
  Payments' own, in PR-3. No schema moves.
- **§15.4** gains the two provider rows, in PR-2.
- **§10.2** gains the `payments-admin` route, in PR-5.
- **`order-review.md`** step 1 cites the read, in PR-5.
- **ADR-024 and ADR-028 do not move**, and no contract's shape does.
- **Appendix C gains no row.** §4.1's tree already names Payments with the
  same five projects.

## 15. What this design deliberately does not do

- **No capture.** Section 1: §3.2 gives no trigger for one.
- **No partial refund and no refund command.** §3.2 closes Payments' Accepts
  column at `AuthorisePayment`.
- **No authorisation expiry.** §9.6's payment timeout and the cancellation it
  leads to are the one clock on that wait.
- **No `Pending` intent and no webhook.** Section 4's rejected approaches.
- **No manual void endpoint.** Section 1: the runbook's manual refund stays
  at the provider.
- **No Redis.** Section 2.
- **No purge of the three tables.** Section 7.
- **No nightly contract test.** §12's table pairs WireMock.Net with one
  against the provider's sandbox, and there is no provider; it is owed the
  day section 1's real provider is chosen, beside the adapter pointed at it.
