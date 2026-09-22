# Shipping — the fifth service

Design spec, frozen at write time. No PR number: Appendix C is closed and
says a gap the plan left is a pull request whose body says so, so this is
dated and named for its subject, like the Inventory and Payments specs
beside it. Where this document and the blueprint disagree, the blueprint
wins.

**What is already decided, and where.**
[§3.2](../../backend-architecture/03-bounded-contexts.md) gives Shipping its
row: it owns `Shipment` and `TrackingEvent`, publishes `ShipmentDispatched`
and `ShipmentDelivered`, consumes `OrderConfirmed`, accepts no command, and
is *Conformist* to a carrier's API. Both of its contracts exist in
`Common.Contracts.Shipping.V1`. The same section, with
[§4.1](../../backend-architecture/04-solution-structure.md),
[§13](../../backend-architecture/13-observability.md),
[§15.3](../../backend-architecture/15-cicd-deployment.md) and
[ADR-051](../../backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md),
says Shipping has **no API at all**: its host is `Shipping.Worker`, its chart
has no Service and no Ingress, and its only listener is the health endpoint.
[ADR-052](../../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
decides where the delivery address comes from and that no consumer makes the
read, and
[ADR-053](../../backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
that a retention window is a value the deployment is given and a carrier is
a processor with a place. `OrderStatus`'s remarks and `ShipmentDelivered`'s
own say Ordering never learns of a delivery. Four things are already waiting
on the service: Ordering's saga arms `DespatchTimeoutDelay` at confirmation
and finalises on `ShipmentDispatched`, Inventory's
`ShipmentDispatchedHandler` fulfils its reservation on the same event,
ADR-051's projection is owed both events, and `tools/new-service` refuses
the name `Shipping` until it can render a Worker.

This document does not restate any of that. It records what those chapters
leave open and the decisions taken on each, so the PRs below can be argued
against something written down.

## 1. What the blueprint leaves open, and the answers

**Who the carrier is.** A **simulator over HTTP**, as Payments' provider is:
a WireMock.Net image with JSON mappings behind a typed `HttpClient` adapter,
so the anti-corruption layer meets a network, a timeout and a foreign
vocabulary, and no credential or external dependency enters CI. A real
carrier is the same port with a second adapter, and that adapter is where a
second carrier's differences — its transliteration of a label, its status
words — belong.

**What despatches a shipment.** The carrier does, and Shipping learns of it
by asking. `OrderConfirmed` creates the shipment; a worker **books** it with
the carrier; the carrier's tracking feed later says `collected`, and that
fact is the despatch. An operator endpoint that marks a shipment despatched
was considered and is refused by the five places above that give Shipping
no API — it would cost a Service, a gateway route, a permission and four
chapters to let a person type what the carrier already reports. The clock
on the wait is already written: the saga's `DespatchTimeoutDelay` raises
`not_despatched` when no despatch arrives.

**How tracking arrives.** A polling worker over the port. A webhook is the
variant named and deferred, and what it would owe is said here so that
deferring it is a decision: it is the first inbound no JWT covers, so a
signature over the raw body, a replay window, an `anonymous` route at the
gateway said out loud — and a Service, which is the API §3.2 says Shipping
does not have.

**Whether tracking is published as it arrives.**
[§10.7](../../backend-architecture/10-api-gateway.md) leaves this one
question to this spec. It is not: the two milestones are the timeline. A
third contract would carry a carrier's vocabulary onto the bus for one
screen, and the buyer already holds a tracking number to take to the
carrier, whose feed it is.

**What a cancellation after confirmation does.** Shipping consumes
`OrderCancelled`, which §3.2's Consumes cell gains in the PR that binds it.
Section 6 states both interleavings. No second operator queue opens: the
saga already raises `ReviewReasons.CancelledAfterConfirmation` when a
despatch meets a cancelled order, so Shipping publishes `ShipmentDispatched`
as normal and that row stays the single record.

**Whether Shipping needs Redis.** No. Its two workers claim rows under a
lease in SQL, it caches nothing and it has no HTTP write command for §8.5's
keys, so it reaches neither instance — §2's diagram already draws it that
way. The scaffold's `AddRedisConnections` is stripped as Payments' was, and
§2's sentence naming Payments gains Shipping.

## 2. Places the blueprint and the tree move

Each rides in the PR that makes it true; section 13 lists them by PR.

- **`tools/new-service` gains a Worker mode**, and `Shipping` comes off the
  scaffold's refusal; `Notifications` stays on it, because §4.1 gives that
  service no Domain project and that is a second mode, not this one. §4.5 says
  the mode joins the script with the first worker built, so PR-1 is that PR, and
  anything hand-fixed after the render is a defect fixed in the scaffold in the
  same change.
- **The scaffold renders the outbox gauges.** Catalog holds
  `deploy/observability/check.py`'s one `OUTBOX_METRICS_EXEMPT` entry, on
  the argument that a service-local `OutboxMetrics` would render into every
  new service. Shipping hosts a dispatcher and those gauges are the only
  alerted latency signal it has, so PR-1 registers them in the template and
  deletes the entry.
- **The BFF's `Identity/` types move to `Common.Infrastructure`**, as
  ADR-052's consequences say, in a PR of their own: `ITokenCache`,
  `CachingTokenClient`, `ClientCredentialsHandler` and
  `ServiceIdentityOptions`, with their tests. `ServiceOptions`' remark that
  the BFF holds the only options type, and §4.1's tree comment that it is
  the only host with client credentials, move with them.
  **`Common.Infrastructure` and not `Common.Web`, and the choice is argued
  rather than assumed.** `Common.Web` is the easier home — it already carries
  the framework reference `IHttpClientFactory` rides in, and every host
  references it — but the next two consumers of these types are
  `Shipping.Infrastructure`'s address adapter and Notifications', and
  `Common.Web` is the host block: §4.1's tree calls it "Host defaults" and
  "Referenced by every host", and every reference to it in the solution is a
  host's. The edge the other way already exists — every `*.Infrastructure`
  project references `Common.Infrastructure` and none references `Common.Web`
  — so the grant's new home needs no new kind of edge and its alternative
  would introduce one. **§4.2's table says none of this, and no gate enforces
  it**: the `*.Infrastructure` row forbids another service's projects and
  nothing else, and the architecture suites cover Domain, Application and the
  composition root, not this. The convention is what §4.1 states and what the
  solution does, which is why it is argued here rather than cited as a rule.
  The cost is a `Web.Bff` →
  `Common.Infrastructure` edge no host draws today, dragging MassTransit,
  Redis, Dapper and `HybridCache` into the BFF's restore; it is accepted and
  written down as `Common.Web.csproj` writes down Gateway.Api's EF Core
  restore, and the pin that edge needs is Class E's own pull request.
- **Ordering gains a gRPC method, `DeliveryAddresses.Get`,** under
  `orders:delivery-address` — its first, and the platform's second gRPC
  server — and the realm gains the `shipping-worker`
  client holding that one role. ADR-052 argues the shape.
- **§3.2's Consumes cell gains `OrderCancelled`; §2.2's diagram gains a
  carrier node, Shipping's edges to Ordering and to that node, and
  `SHP -.-> IDP`; §11.5's table of realm objects and §15.4's inventory gain
  the client and the carrier's two keys.** The carrier node is owed because
  §2.2 draws none — the one in the corpus is §2.1's — and the third edge
  because ADR-052's §2.2 row is about the client-credentials edge that
  section draws from the BFF and from nothing else.

## 3. The PR sequence

Eight, where Inventory took five and Payments six: the worker mode is the
scaffold's, the identity move is a shared mechanism and may not ride with a
service — and its package pin is a class of its own — and the address
owner's endpoint is Ordering's slice and not Shipping's. Each row names its
[change class](../../change-locality.md); the touch set is the PR body's.

| PR | Subject | Class |
|---|---|---|
| 1 | `feat(shipping): fifth service from the scaffold's worker mode` — the mode in `tools/new-service`, with the outbox gauges in the template and Catalog's exemption deleted; the render with `AddRedisConnections` stripped and §2's sentence amended; `Shipment` and `TrackingEvent`, the typed ids, the state machine and the first migration; the Compose pair; `ci.yml`'s filter, outputs, matrix legs and the `images` job's own `if:` | A+D+E |
| 2 | `feat(shipping): the carrier anti-corruption layer and its simulator` — `ICarrierGateway`, the typed `HttpClient` adapter, `CarrierHop`'s five numbers, the translation and its bounds, the carrier counter and its `AddMeter` line, `deploy/compose/carrier-simulator/` with its line endings declared, §15.4's two rows. Nothing calls it yet | A+D+E |
| 3a | `chore(deps): Common.Infrastructure takes Microsoft.Extensions.Http` — the pin, the project file and the Appendix B row, because the token client resolves `IHttpClientFactory` and that building block takes no framework reference | E |
| 3b | `refactor(identity): client credentials move to Common.Infrastructure` — the four types and their tests out of `Web.Bff`, a carrier for the authority key's name that the building block may not spell, the BFF re-pointed, the secret scan's four entries re-pathed, no behaviour moved | B+D |
| 4 | `feat(ordering): a delivery address is read under orders:delivery-address` — `delivery_addresses.proto` and its service, the query behind it, the permission, the realm's role and the `shipping-worker` client, `realm_check.py`'s predicate, the gateway test that no route reaches the method, `docs/secrets.md`'s rows, and the Keycloak-issued-token tests in both directions | A+D+E |
| 5 | `feat(shipping): consume OrderConfirmed and OrderCancelled, and book the shipment` — `shipping-events`, the two consumers, `DeliveryAddresses`, the address adapter with `AddressHop`, the fulfilment worker that reads the address and books, the void and the carrier cancel, §3.2's cell and §2.2's edges | A+D+E |
| 6 | `feat(shipping): tracking, and the two events` — the tracking worker and its lease, the monotonic promotion, `ShipmentDispatched` and `ShipmentDelivered` through the outbox, the retention pass over addresses and tracking events, the cross-service container test | A+D+E |
| 7 | `feat(deploy): Shipping's chart, deploy target and canary` — `deploy/helm/shipping` with `service.enabled: false` and `redis.enabled: false`, the library chart's `carrier` and client-credentials capabilities, the umbrella, `smoke.sh`'s lists, `deploy.yml`'s option, the canary map, §13.6's first queue-backlog and delivery-lag rules with the one runbook they share | D |

**Order.** 1 → 2, and 3a → 3b → 4, in either interleaving; 5 needs all of
them; then 6 and 7. PR-3a, PR-3b and PR-4 touch no Shipping path, so a
second session can take them while the first builds PR-1 and PR-2. PR-3 is
two pull requests because its one plan is honestly three classes and
`docs/change-locality.md` admits `A+D+E` alone; the pin is Class E's whole
touch set on its own.

**Why the aggregate lands in PR-1 and nothing drives it until PR-5.**
Payments' PR-1 carried its order record for the same reason: the scaffold's
proof is a service that migrates and starts, and a render with no table of
its own proves the template and not the service.

**Why CI joins PR-1 and Helm does not** is the Inventory spec's argument
unchanged: the pipeline gate refuses a service directory no filter matches,
and `smoke.sh` checks its chart list against the charts on disk in both
directions, so a chart is owed the day it is added and not before.

## 4. Where the outbound calls sit

**In two workers, and in no consumer.** ADR-052 decides it for the address
read, and the same argument holds for the carrier with more force: a
consumer that called the carrier would hold its endpoint's slot across a
third party's latency. So Shipping's consumers only write rows, and a
carrier outage stops a worker and nothing else — the bulkhead is the
design, not a filter on it.

Each worker is a `BackgroundService` in `OutboxDispatcher`'s shape, and the
shape is the point:

- **The claim is taken before the call, and it is a lease.** A pass claims
  a batch with `UPDLOCK, READPAST, ROWLOCK` and stamps `LockedUntil`, so a
  second replica's pass skips the rows and a pod killed mid-call strands
  nothing: the lease lapses by itself. The lease is longer than the hop's
  total, and a test holds that inequality.
- **A failed row backs off and stays.** `Attempts` and a `NextAttemptAt` of
  its own — the dispatcher stamps its backoff on `LockedUntil`, and these
  two workers separate the lease from the wait — with the dispatcher's
  `2^min(Attempts, 8) × 5 s`. Nothing is
  abandoned by count: the shipment's deadline is the saga's, and a row
  that outlives it is already a review row in Ordering.
- **The loop survives its tick.** It catches per pass and its filter asks
  the token, not the exception's type, because no host sets
  `BackgroundServiceExceptionBehavior` and the default turns one escaped
  exception into a stopped host. A pass fits the thirty-second drain, which
  the hop's total already buys.

**A call that writes carries a key derived from the aggregate**:
`book:{ShipmentId}` and `cancel:{ShipmentId}`, in
`AuthorisationRequest.IdempotencyKey`'s shape. The crash that doubles the
call is the one between the carrier's answer and the commit; the next pass
repeats the call under the same key and receives the first answer.

**Rejected.** The call inside the consumer under delayed redelivery, for
ADR-052's reasons. One worker doing every step, because booking is paced by
new orders and tracking by the carrier's rate limit, and one loop would
make the slower the other's ceiling.

## 5. Domain

**`Shipment`**, keyed by `ShipmentId` with `OrderId` unique — one shipment
per order, which is all `OrderConfirmed` can mean while an order has one
address. Its states and the only moves between them:

| From | On | To | Raises |
|---|---|---|---|
| — | `OrderConfirmed` | `Pending` | — |
| — | `OrderCancelled` | `Voided` | — |
| `Voided` | `OrderConfirmed` | `Voided` | — |
| `Pending` | the carrier books it | `Booked`, with the carrier's reference and tracking number | — |
| `Pending` | the address owner or the carrier answers that it cannot be done | `Unfulfillable`, with a reason | — |
| `Pending` | `OrderCancelled` | `Voided` | — |
| `Booked` | `OrderCancelled` | `Booked`, with `CancellationRequestedAt` | — |
| `Booked`, cancellation requested | the carrier cancels it | `Voided` | — |
| `Booked`, cancellation requested | the carrier answers that it has gone | `Booked`, with `CancellationRefusedAt` | — |
| `Booked` | a `Collected` tracking event | `Dispatched` | `ShipmentDispatchedDomainEvent` |
| `Booked` or `Dispatched` | a `Delivered` tracking event | `Delivered` | the despatch event first when it was never raised, then `ShipmentDeliveredDomainEvent` |

The second and third rows are §8's tombstone: `OrderCancelled` before
`OrderConfirmed` creates the shipment already `Voided`, and the late
confirmation finds it and does nothing. That no-op is a row of the table
rather than a case of the sentence below, because the two commuting is the
whole reason the tombstone exists and §12 drives the domain suite from this
table.

**Every other arrival is a no-op that logs and returns, never a throw.** A
fact already superseded — a `Collected` after `Delivered`, a second
`OrderCancelled`, a cancellation of a delivered shipment — thrown from a
worker is a row retried for ever, and thrown from a consumer is a
redelivery loop and then `_error`. `Voided`, `Unfulfillable` and
`Delivered` are terminal.

**`TrackingEvent`** is an entity of the shipment, keyed by
`(ShipmentId, CarrierEventId)`: the carrier's id, the platform's own
`TrackingStatus`, and the carrier's timestamp. The key makes a repeated
page free; it orders nothing, which is why the state machine above is
monotonic by rank and not by arrival.

`TrackingStatus` is closed: `Collected`, `InTransit`, `Delivered` and
`Unrecognised`. A carrier word the translation does not know is stored as
`Unrecognised` and moves nothing — a carrier adds statuses on its own
schedule, and a conformist that faults on a new one stops tracking every
shipment until a deploy.

## 6. Cancel against despatch

Two services decide independently with no transaction between them, so "a
cancelled order has no despatched shipment" is an invariant nobody can
enforce and somebody can detect. Shipping's half, in both interleavings:

- **Cancel, then despatch.** A `Pending` shipment is voided at once and is
  never booked. A `Booked` one asks the carrier to cancel; until the
  carrier answers, tracking continues, because the parcel may already be
  moving.
- **Despatch, then cancel.** The shipment is `Dispatched` or `Delivered`,
  the cancellation is a no-op, and the goods move. The saga's
  `CancelledAfterConfirmation` review row is the one record, and
  `order-review.md` the one procedure.
- **The carrier says it is too late.** `CancellationRefusedAt` is stamped,
  tracking goes on, `ShipmentDispatched` is published when `Collected`
  arrives, and the saga escalates exactly as in the second case.

Whichever order the two facts arrive in, the terminal state is the same,
and a test runs both.

## 7. Persistence

Schema `shipping`, database `Shipping`, both connection keys, as the
scaffold names them. Three tables beside the scaffold's outbox, inbox and
marker tables:

| Table | Key | Holds |
|---|---|---|
| `Shipments` | `ShipmentId`; `OrderId` unique | `Status`, `CarrierReference NULL`, `TrackingNumber NULL`, `UnfulfillableReason NULL`, the three cancellation and terminal instants, `Attempts`, `NextAttemptAt`, `LockedUntil`, `NextPollAt`, `RowVersion` |
| `TrackingEvents` | `(ShipmentId, CarrierEventId)` | `Status`, `OccurredAt`, `RecordedAt` |
| `DeliveryAddresses` | `OrderId` | `CustomerId`, `Line1`, `Line2 NULL`, `City`, `PostalCode`, `Country char(2)`, `FetchedAt` |

**`DeliveryAddresses` is the one place personal data lands in this service**,
which is why it is a table of its own and not five columns on the shipment:
erasure and retention each delete a row and leave the shipment's record whole.
Every text column is `nvarchar`, and a Kazakh-script address — `ә ғ қ ң ө ұ ү һ
і` — round-trips through the table and reaches the simulator's journal intact in
a test. It is written and read by raw statements through a port,
`IDeliveryAddressStore`, following `IUnitOfWork.ExecuteRawAsync`'s rule for a
table with no aggregate behaviour. It carries `CustomerId`, taken from the
owner's reply, which ADR-052 puts there for this reader, for erasure's sake
alone; `Shipments` holds none, and the carrier is shown an address and the
shipment's id and nothing else.

**Retention is a value of the deployment, refused rather than clamped**
(ADR-053). `ShippingJurisdictionOptions` is the one options class that record's
rule 1 gives a service, and Shipping's holds two statutory windows and nothing
else, since it renders no message: an address is deleted its window after its
shipment turns terminal, and tracking events theirs after delivery. Both are
`[Required]` with a stated bound, validated at start as §15.4 validates
`ServiceIdentityOptions`, and neither joins `RetentionPolicy`, whose windows are
housekeeping. The pass deletes by identity, as `RetentionPurgeService` does.
`InboxWindow` is the scaffold's and needs no widening, because nothing here
parks a message. PR-6 is the first pull request to bind such a class, so it
amends §15.4's sentence that `ServiceIdentityOptions` is the only options type
and its callout's close, as ADR-053 says that pull request does.

**Erasure** is §11.7's consumer shape, owed with that extension like the
rest of it: `DELETE` from `DeliveryAddresses` by `CustomerId`, which leaves
every shipment's own record whole. The path is named beside the table's
creation because ADR-052 asks for that.

The migrations are named for their tables and emitted by `dotnet ef
migrations add`: `AddShipments` with `TrackingEvents` in PR-1,
`AddDeliveryAddresses` in PR-5. A first migration rewritten in review is
followed by `down -v` before the migrator's answer is believed.

## 8. Messaging

**`shipping-events`** is one queue binding `OrderConfirmed` and
`OrderCancelled`, declared as §9.5 prints a receive endpoint: the inbox
filter outside the in-memory outbox, and `RetryPolicy.Standard` with the
mapping exceptions excluded. **It has no delayed redelivery and no second
ladder, because neither consumer can meet a fault that is a wait**: each
writes one row in its own database. `OrderCancelled` before
`OrderConfirmed` writes a `Voided` tombstone, and the late confirmation
finds it and does nothing — the two commute, as Payments' record writes do.

`ShippingIntegrationEventMapper`'s registry is §3.2's Publishes column and
exactly it: two entries. `MessagingRegistrationTests` asserts every cell of the
Consumes column has an `AddConsumer`, and the worker's broker-binding test over
a live broker that each is bound to `shipping-events` — the `ConfigureConsumer`
half cannot be read off the container, since the harness replaces the
`UsingRabbitMq` callback. The broker account `shipping-svc` takes
`ordering-svc`'s shape under a `shipping-` prefix from PR-1, since PR-5's queue
needs it and `check_permissions.py` holds the entry to the code either way.

## 9. The anti-corruption layers

There are two, and each failure either can meet is classified here —
transient, dead dependency, or an *answer* — because only a defect may
reach `_error`, and nothing in this service throws into a queue at all.

**The carrier port** is `Shipping.Application`'s, in the domain's words:
`BookAsync` returns `Booked(reference, trackingNumber)` or
`Refused(reason)`; `CancelAsync` returns `Cancelled` or `TooLate`;
`GetEventsAsync` returns the page already translated. A transient fault
throws `CarrierUnavailableException`, which the worker's backoff owns.

| The carrier answers | The port returns | Class |
|---|---|---|
| 201 to a booking | `Booked` | — |
| 422 `address_not_serviceable` | `Refused` | an answer: `Unfulfillable` on the row |
| 200 to a cancel | `Cancelled` | — |
| 409 `already_collected` | `TooLate` | an answer |
| 404 to an events read | an empty page, logged | an answer: the carrier has not heard of it yet |
| 408, 429, 5xx, a timeout, a refused connection, an open circuit | throws `CarrierUnavailableException` | transient or dead: the row backs off |
| a body that is not the agreed shape | throws `CarrierUnavailableException` | treated as the carrier being wrong, never stored |

**What the carrier sends is a stranger's input.** The adapter bounds the
body at `CarrierHop.MaxAnswerBytes`, bounds every string it keeps, refuses
an event whose timestamp is later than the clock allows, maps a status it
does not know to `Unrecognised`, and **stores no URL**: a carrier-supplied
link in a customer's email is a phishing primitive, the contracts carry a
tracking number and nothing else, and whoever renders a link rebuilds it
from a host its own configuration allow-lists.

**`CarrierHop`** holds the hop's numbers in one class, in `ProviderHop`'s
shape with the breaker added: the attempt timeout, the retries, the capped
delay, the total — strictly below `ServiceOptions.OperationTimeout`, and
outside §9.7's one-to-two-second attempt band for §9.7's own reason, that a
third party behind an ACL is sized to the wait above it and not to a
waiting caller — and the breaker's ratio, minimum throughput, sampling
window and break, **sized to a worker's call rate** and asserted able to
open, which `ProviderHop` does not say of its own. It also names the two
intervals a worker runs at: the fulfilment tick, and the tracking poll
interval, which is a latency number before it is a load number — nothing
downstream learns of a despatch sooner than the next poll — and is picked
against §13.7's event target and the carrier's rate limit together.

**The address port** is `IDeliveryAddressSource.GetAsync(orderId)`, returning
`Found(address, customerId)` — the id ADR-052 puts on the reply for the reader's
erasure row — or `NoSuchOrder`; its adapter is the generated gRPC client over
Ordering's method, the `.proto` linked as `pricing.proto` is into `Web.Bff`,
with `ClientCredentialsHandler` inside its resilience pipeline and
`AddressHop`'s five numbers, inside §9.7's bands because Ordering is a peer and
not a third party. ADR-052's five outcomes are its contract: `NotFound` — no
such order, a cancelled one, or one whose address erasure has cleared — is
`NoSuchOrder` and the shipment turns `Unfulfillable`; anything transient throws
and the row backs off; and `Unauthenticated`, `PermissionDenied` or a refused
token throws `AddressSourceRefusedException`, which backs off as a transient
fault does and increments `shipping.address.refused`, because a revoked grant is
a defect somebody must see and not an outage to wait out. The token client holds
the token's `permission` claim to exactly `orders:delivery-address`, as that
record decides.

**Each outbound dependency**, classified as
[§9.8](../../backend-architecture/09-messaging.md) classifies a failure, with
ADR-053 rule 3's place of processing in the last column:

| Dependency | Unreachable | Answers no | Chose | Runs |
|---|---|---|---|---|
| Ordering, for the address | the shipment stays `Pending` and backs off; consumers unaffected | `Unfulfillable: no_such_order`; a refused credential backs off and is counted | availability | the same deployment, by ADR-053 |
| The carrier, to book | the shipment stays `Pending` and backs off | `Unfulfillable`, with the carrier's reason | availability | a chart value; a processor shown an address and a shipment id, and the spec of a real adapter names its country |
| The carrier, for events | the poll backs off; no event is late by more than the outage | an empty page | availability | as above |
| The carrier, to cancel | the request stays on the row; tracking continues | `TooLate`: the saga's review row is the record | correctness of the record over speed of the void | as above |
| Keycloak, for the client token | the address read fails as transient | a refused credential is a start-up or deployment fault, logged without the secret | availability | the same deployment |

**ADR-053 rule 3's naming of the carrier's country is owed with the first
real adapter**, because the simulator runs wherever Compose does and is shown
no real address — stated here so that the deferral is a decision and not an
omission somebody meets in a review.

**The simulator** is `deploy/compose/carrier-simulator/`: the WireMock.Net
image at the tag `Directory.Packages.props` pins the package to,
`mappings/*.json` and a README, with its line endings declared in
`.gitattributes` in the same PR. It holds no state, so the booking's
reference carries the script and the later reads key on it. The script is
the postal code, because it is the one field the carrier sees that a person
placing a Compose order controls:

| Postal code | Answer |
|---|---|
| `SIM-REFUSED` | the booking answers 422 `address_not_serviceable` |
| `SIM-DOWN` | the booking answers 503 |
| `SIM-SLOW` | the booking answers after a delay past `CarrierHop`'s total |
| `SIM-TRANSIT` | events: `collected` and nothing after it |
| `SIM-REVERSED` | events: `delivered` on the page before `collected` |
| `SIM-STRANGE` | events: a status nobody agreed, a timestamp from next year, a link on a foreign host |
| `SIM-LATE` | a cancel answers 409 `already_collected` |
| any other | booked; events `collected` then `delivered` |

A plain Compose checkout therefore takes a placed order to `Shipped` with
no test double in the path, and the named codes are how a person watches
each failure. The tests start WireMock.Net in process over the same
`mappings` directory.

## 10. Configuration and deployment

**PR-1's keys are Payments', renamed.** `ConnectionStrings__Shipping`,
`ConnectionStrings__ShippingMigrator`, `ConnectionStrings__RabbitMq` with the
`shipping-svc` account, `Identity__Authority` and `OTEL_EXPORTER_OTLP_ENDPOINT`.
PR-2 adds `Carrier__BaseUrl` and `Carrier__ApiKey`; PR-5 adds
`AddressSource__BaseUrl` and the three `Identity__Client__*` keys; PR-6 adds
`Jurisdiction__AddressRetention` and `Jurisdiction__TrackingRetention`. **Every
new credential is `docs/secrets.md`'s five places**, and a row in its
local-development exception table, split as §15.4's own rule splits them — a key
joins when a host's code reads it. The carrier's key is PR-2's in every place
but the chart's. The client secret is minted in PR-4, which takes the realm, the
rotation row, the exception row and §15.4's inventory rows; its Compose and
fixture places are PR-5's, with the keys the worker first reads there; and the
chart's place cannot exist before the chart and is PR-7's, as Payments'
`paymentProvider` capability was its deploy PR's.

**No port is published.** Nothing dials a worker, so the health endpoint is
bound inside the container and never mapped, and the worker mode takes no
`--port`. The Compose unit declares no `healthcheck`, as no API unit does: the
runtime image is chiselled and carries no probe binary, and readiness is PR-7's
probes' to assert.

**PR-7's chart is Ordering's less the Service**: `service.enabled: false` and
`ingress.enabled: false`, both written down as §15.3 asks, and `redis.enabled:
false`. Every key the host binds at start arrives as a capability the render
refuses to leave empty, on the `paymentProvider` pattern: `carrier` for the
carrier's two keys, `addressSource` for Ordering's gRPC address, `jurisdiction`
for ADR-053's two windows, and the client credentials through the capability the
BFF's chart already uses — four, because a chart that renders without one of
them is a pod that never starts, which is the shape `_helpers.tpl` exists to
refuse. **The scaling values are a decision and not a copy**: CPU is the wrong
signal for a workload that waits on a queue and a carrier, so autoscaling is
off, the replica count is three for availability, and the backlog rule below is
how anyone finds out it is too few. The canary row declares `consume` and
carries an `httpExemption`, as Inventory's does. **The readiness set is SQL and
the bus, and a test asserts the carrier, Ordering and Keycloak are not in it**:
they are shared by every replica, so one outage would pull every pod and then
block the rollout carrying the fix.

## 11. Observability

**The outbox gauges** arrive with the render, by section 2.

**Three instruments on a `Shipping.Outbound` meter**, in Payments' form:
`shipping.carrier.unavailable`, incremented per attempt that ends
transient, `shipping.address.refused`, which section 9 argues, and
`shipping.shipments.waiting`, an observable gauge of rows
past their first backoff, by state. The meter is named for what the three
share — the work that leaves this service — because every meter is named for
its subject and two of the three are not the carrier's. §13.2's export names
meters one by one, so PR-2 adds the `AddMeter` line.

**Shipping's latency number is delivery lag, and PR-7 gives it the first
rule that reads it**: one alert over `messaging_delivery_lag_seconds_bucket`
and one over a working queue's depth, the second in `OutboxGrowth`'s shape —
over a threshold **and** rising. They share one runbook, declared in
`SHARED_RUNBOOKS` with its reason, as `error-rate.md` is. What stays owed
is said in the runbook: delivery lag stops when a consumer starts, so it
never sees a worker's wait on a carrier, and the waiting gauge is the
signal for that.

**No log line holds an address**, as an attribute or inside an exception's
text: the log takes the shipment's id and the order's id. `SensitiveKeys`
is not widened. A test exports the logs of a run over a known address and
finds none of it.

## 12. Testing

By [§12](../../backend-architecture/12-test-strategy.md)'s layers; the
container tests are `Category=Integration` and never skipped. The suites
are `Shipping.Domain.Tests`, `Shipping.Application.Tests` and
`Shipping.Worker.Tests`, with `Shipping.TestSupport` beside them.

- **Domain**: every row of section 5's table, and every refused arrival as
  a no-op; **the shuffled feed** — every permutation of a shipment's
  tracking events reaches the same terminal state and raises the same set
  of events, despatch before delivery.
- **Application**: both consumers against a fake store, in both orders; the
  mapper registry against the two contracts; the architecture tests.
- **Worker**, over SQL Server and RabbitMQ containers and an in-process
  WireMock.Net:
  - the adapter against every row of section 9's two tables, the hostile
    page included, with nothing of it stored;
  - **the inequality** over `CarrierHop`'s and `AddressHop`'s constants, in
    `Every_attempt_and_every_bounded_delay_fit_inside_the_total`'s shape,
    each lease longer than its hop's total, and a stalled carrier failing
    inside the total;
  - **an opened circuit makes no call**, in `UpstreamRetryTests`' shape;
  - **two workers overlapping claim one row once**, in
    `A_row_still_being_delivered_is_not_claimed_by_a_second_pass`'s shape —
    staged, not two passes back to back; **a pass that throws leaves the
    host running**; **a lapsed lease is taken by another pass**;
  - **the owner is taken away**: with Ordering's stub refusing, the
    shipment stays `Pending`, every `_error` queue is empty, and when the
    stub recovers the shipment books; `NoSuchOrder` is terminal and is not
    retried;
  - **a crash between the carrier's answer and the commit** books once at
    the carrier: the simulator's journal holds two requests under one key
    and the table one reference;
  - both interleavings of section 6, and the `SIM-LATE` case;
    - the Kazakh-script round trip and the log export of section 11;
  - **the fixture is ADR-053's made-up deployment**: it binds
    `ShippingJurisdictionOptions` from invented windows, an address whose
    country is `ZZ` books at the simulator, and a missing or zero window
    fails the host at start;
  - `MessagingRegistrationTests`, and the readiness-set assertion.
- **PR-4's**, in `Ordering.Api.Tests` and `Web.Bff.Tests`' Keycloak fixture:
  `Unauthenticated` with no token, `PermissionDenied` with a user's token
  holding every user permission, the address with the client's — all three,
  because the first alone passes against a host that stopped routing; a token
  Keycloak issued to `shipping-worker` is accepted and one issued to a client
  without the role is refused.
- **`Platform.IntegrationTests`**, in PR-6: on a real `ShipmentDispatched`,
  Ordering's saga moves **and** Inventory's handler fulfils the reservation,
  neither depending on the other having run.
- **The scaffold's suite**, in PR-1: the worker render's tree, and that the
  licence gate's walk, the secret scan's allow-list trees, the comment gate's
  readers, the coverage filter's pattern, the pipeline gate's glob and the
  architecture tests each cover the Shipping projects — a test whose subject is
  what each gate is looking at. Four of the six hold no list of services at
  all, so the assertion is over the selector and never over an enumeration.

## 13. The chapters that move, and the ones that do not

**ADR-052's closing table is the owner of "the places that say the BFF is
alone", and this section restates none of it.** What that record left to the
pull request that builds each service is *which* pull request takes each row,
and that is the one thing decided here. Nothing below copies a row's wording:
the left column names the owner and the right names the pull request. A row
nobody takes is a stale claim at best, and the rows ADR-052 marks **asserted**
are gates that go red — which a builder must not learn from CI, which is why
that record wrote the list out and why this table finishes it.

| ADR-052's row | Taken by |
|---|---|
| §2.2 | 5 |
| §3.2 | 5 |
| §4.1 | 3b for the identity types; 5 for the tree comment's "ONLY host that calls a service" |
| §9.7 | 5, both sentences |
| §11.5 | 4, the table of realm objects and the three sentences that count its hosts |
| §12 and ADR-023 | 5, and ADR-023 gains no edit |
| §14.1 and §14.2 | 5 |
| §15.1 and §15.4 | 7 for §15.1; §15.4's rows and sentence are below |
| `_helpers.tpl` — asserted | 7 |
| `smoke.sh` — asserted | 7 |
| `RealmClientTests` — asserted | 4 |
| `RealmImportTests` — asserted, both tests | 4 |
| `KeycloakIdentityTests`' comment | 4 |
| `deploy/helm/catalog/` | 4 |
| `deploy/compose/README.md` | 5 |
| `docs/runbooks/latency.md` | 5 |
| `src/BFF/Web.Bff/` and `render.py` | 3b for every comment under `src/BFF/Web.Bff/`, both halves, and the one diagnostic string that says the same; 5 for `render.py`'s two |
| §11.7 | 5 |
| `docs/secrets.md` | 4 |
| `docs/repo-map.md` and `CLAUDE.md` | 4 for the gRPC-server halves; 5 for the BFF's |
| `deploy/helm/ordering/values.yaml` | 4 |
| `realm-export.json` | 4, the `web-bff` description included |

**The assignments that are not obvious, and why.**

- **§11.5's counting sentences go to PR-4 with §11.5's table**, not to PR-5
  with the other synchronous-hop sentences: the section's table row, its
  count of hosts and its prose make one claim, and splitting them across two
  pull requests leaves the paragraph arguing against its own table. There are
  three, not two — the sentence that fixes the count at one stands between
  the paragraph that names the hosts and the callout that already argues what
  moving the count costs, and it is the easiest of the three to leave behind.
- **§15.1 goes to PR-7.** That sentence describes what `smoke.sh` asserts,
  and `smoke.sh` is PR-7's; the sentence and the assertion move together or
  one of them is wrong for three pull requests.
- **§12's sentence goes to PR-5 and ADR-023 gains no edit.** The `.proto`
  linked into `Shipping.Infrastructure` is ADR-023's own form, so it is the
  second relationship that record says would be judged with Shipping; ADRs
  are superseded and never rewritten, and ADR-052 already points at Shipping
  for this question.
- **§11.7's erasure diagram step goes to PR-5**, where Shipping's step stops
  being "anonymise Shipment recipient" and becomes the delete of the
  `DeliveryAddresses` row that section 7 decides.
- **The asserted rows split between PR-4 and PR-7 by what turns each red.**
  `RealmClientTests.It_is_the_only_service_account_client_in_the_realm`,
  `RealmImportTests.No_client_ships_a_secret_but_the_one_whose_grant_needs_one`
  and
  `RealmImportTests.The_permission_vocabulary_is_a_closed_set_of_client_roles`
  go red on the realm edit, which is PR-4's; `_helpers.tpl`'s `fail` and
  `smoke.sh`'s credential assertions go red on a second credentialed chart,
  which does not exist before PR-7.
- **`docs/repo-map.md` and `CLAUDE.md` are split.** Catalog stops being the
  one gRPC server in PR-4 and the BFF stops being the one synchronous caller
  in PR-5, and each half moves in the pull request that makes it false.
- **`src/BFF/Web.Bff/`'s comments are not split, and PR-3b takes both
  halves.** The obvious reading splits them the way the documents split — the
  credentialed-host half to PR-3b, the one-synchronous-caller half to PR-5 —
  and the class system forbids it. PR-5 is Shipping's `A+D+E` slice, and
  [`docs/change-locality.md`](../../change-locality.md) §3 says the touch set
  of `A+D+E` names the one service's paths and the files outside the slice it
  edits; the BFF is a host, not that service, and Class B is the class whose
  set reaches `src/BFF/**`. PR-3b is Class B, is rewriting the BFF's identity
  wiring anyway, and is a pull request in which ADR-052 has already decided
  the BFF is not alone — so the sentence a rewritten comment would have to
  stop making is one PR-3b can make false and PR-5 cannot reach.
  `render.py` stays with PR-5 because the tools tree is Class D's, which that
  PR's touch set declares. The diagnostic string in `CachingTokenClient`'s
  refusal goes with the comments for the same reason and one more: it counts
  the platform's credential sets, which a building block cannot do, and PR-3b
  is the only pull request whose class reaches the file it moves to.

**The places outside that table.**

- **§4.5** loses its sentence that the worker mode is owed, and **§2**
  names Shipping beside Payments as reaching no Redis, in PR-1.
- **§15.4's inventory** gains the carrier's two rows in PR-2, the amendment of
  the client's three in PR-4, and the two jurisdiction windows in PR-6; the
  `Identity__Client__*` rows are in the table today as the BFF's alone, and
  PR-4 is what makes their Required column name the obligation's shape
  instead. **§15.4's sentence that the solution has one options type, and its
  callout's close,** move in PR-6, which binds the second.
- **Appendix B** gains `Microsoft.Extensions.Http`'s row in PR-3a.
- **`docs/secrets.md`** gains its rotation row and its local-default row in
  PR-4, and its Helm place in PR-7.
- **§3.2's Consumes cell** gains `OrderCancelled`, and **§2.2's diagram** a
  carrier node, Shipping's two edges and `SHP -.-> IDP`, in PR-5.
- **§15.3's sentence that exactly one chart carries client credentials**, and
  the callout under it, move in PR-7, which is where the second one renders;
  §15.3 also names Shipping among the charts with no Redis there.
- **`deploy/compose/README.md`'s host-run port recipe** — the claim that one
  service pins its own ports, and the exports a host run needs — and
  **`smoke.sh`'s listener comparison**, which reads that one service's
  `appsettings.json`, both move in PR-4, which is where a second service
  declares `Kestrel:Endpoints`. ADR-052's table gives that README's row to
  PR-5 and `smoke.sh`'s to PR-7, and neither row is this: those are the
  synchronous call and the credential assertions, and a port recipe is
  neither.
- **§13.6** gains the two rules in PR-7.
- **No contract moves**, and **§10 gains no route**.
- **Appendix C gains no row.** §4.1's tree already names Shipping.

`/validate-blueprint` runs on every PR above that edits a chapter.

## 14. What this design deliberately does not do

- **No operator endpoint and no API.** Section 1.
- **No webhook.** Section 1 says what one would owe.
- **No tracking event on the bus.** Section 1.
- **No recipient's name.** `Order.ShippingAddress` holds none, so the
  carrier is shown none; a name is a field on Ordering's command before it
  is anything here.
- **No Redis.** Section 1.
- **No second carrier.** The port is the seam and a second adapter is the
  PR that proves it.
- **No returns, no partial shipment, no split order.** §3.2 gives one
  shipment per confirmed order and nothing to trigger the others.
- **No erasure consumer.** Section 7 names the path; §11.7's extension
  brings it.
- **No nightly contract test against a carrier's sandbox**, which §12 pairs
  with WireMock.Net, because there is no carrier; it is owed with the first
  real adapter.
