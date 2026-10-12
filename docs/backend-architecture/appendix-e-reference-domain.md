# Appendix E — The reference domain

This appendix owns every fact about the reference domain that a chapter no
longer states: the services and what each is, their contracts, queues, ports,
realms, Compose units and CI filters where no other file holds them, and the
worked examples. A chapter states a rule abstractly and cites a section here
by link, never a value; an adopter replaces this appendix wholesale
([ADR-096](adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md),
[ADR-062](adr/ADR-062-the-domain-is-a-reference-implementation.md)).

Each section below is a skeleton, one per chapter pass, so that a pass fills
the section it cites, replacing its "Filled by" line, and creates none.

## E.1 §2 and §3: the service roster and the context map

This section holds what [§2](02-architecture-at-a-glance.md) and
[§3](03-bounded-contexts.md) no longer state about the reference domain's
services. [§3.2](03-bounded-contexts.md)'s table of what each service owns,
publishes, consumes and accepts stays in the chapter: the
[messaging gate](../../.github/messaging-gate/README.md) reads it there, so it
is its own owner and is not copied here.

**The one-word example (§3.1).** "Product" in Catalog is a rich marketing
object with descriptions, images and categories. "Product" in Inventory is a
SKU and a number. These are not the same concept and must not share a class.

**The context map (§3.1).** One node per context with its type, one edge per
message.

```mermaid
graph LR
    CAT[Catalog<br/><i>Supplier</i>]
    ORD[Ordering<br/><i>Core domain</i>]
    INV[Inventory<br/><i>Supplier</i>]
    PAY[Payments<br/><i>ACL over PSP</i>]
    SHP[Shipping<br/><i>ACL over carrier</i>]
    NOT[Notifications<br/><i>Generic</i>]

    CAT -->|product + price events| ORD
    ORD -->|ReserveStock<br/>ReleaseStock| INV
    INV -->|StockReserved / Failed<br/>StockReleased| ORD
    ORD -->|AuthorisePayment<br/>OrderPlaced| PAY
    PAY -->|PaymentAuthorised / Declined| ORD
    ORD -->|OrderConfirmed<br/>OrderCancelled| SHP
    SHP -->|ShipmentDispatched| ORD
    ORD -->|order events| NOT
    PAY -->|payment events| NOT
    INV -->|StockLevelChanged| CAT
    SHP -->|shipment events| NOT
```

**Each context's type and why it is separate (§3.1).**

| Context | Type | Why it is separate |
|---|---|---|
| **Ordering** | Core domain | This is where the business logic actually lives. Invest here. |
| **Catalog** | Supporting | Different read/write ratio (1000:1), different scaling, different team cadence. |
| **Inventory** | Supporting | Different consistency requirements — stock is the one place contention is real. |
| **Payments** | Supporting | Isolates a volatile third-party API behind an anti-corruption layer. Compliance boundary. |
| **Shipping** | Supporting | Isolates the carrier's API behind an anti-corruption layer, `ICarrierGateway`: a carrier's change lands in its adapter on the carrier's schedule, and never in the domain. |
| **Notifications** | Generic | Not a differentiator. Would be replaced by an off-the-shelf product without regret. |

**What the map leaves out (§3.1).**

- The saga has a fourth round trip the map does not draw, because both of its
  ends are Ordering: `ConfirmOrder` out to the aggregate and `OrderConfirmed`
  back. It is asynchronous on exactly the terms of the edges drawn, and the
  Consumes cell of §3.2's table records it.
- ADR-052's reads are of another kind and are undrawn because the map draws
  messages: a Shipping worker reads a delivery address from Ordering over
  gRPC, and a Notifications worker reads a mailbox from Keycloak's admin API,
  each off every request path
  ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).

**The container view (§2.2).** A node per service but Privacy, which the
diagram does not yet draw (its SQL, RabbitMQ, Redis and OTel edges are
missing too); the chapter draws the
same picture with a node per kind of service.

```mermaid
graph TB
    subgraph Edge
        GW[API Gateway<br/>YARP]
        BFF[Web BFF<br/>aggregation and the order projection]
        IDP[Keycloak<br/>OIDC provider]
    end

    subgraph Services
        CAT[Catalog API]
        ORD[Ordering API]
        INV[Inventory API]
        PAY[Payments API]
        SHP[Shipping worker]
        NOT[Notifications worker]
    end

    subgraph Data
        SQL[(SQL Server<br/>database per service)]
        RC[(Redis cache<br/>allkeys-lru)]
        RK[(Redis coordination<br/>noeviction)]
        MQ[[RabbitMQ]]
    end

    subgraph Telemetry
        OTEL[OTel Collector]
        GRAF[Grafana / Tempo / Prometheus]
    end

    subgraph External
        PSP[Payment provider]
        CAR[Shipping carrier]
    end

    GW --> CAT
    GW --> ORD
    GW --> INV
    GW --> PAY
    GW --> BFF

    BFF -->|gRPC, pricing| CAT

    SHP -->|gRPC, the address read| ORD
    SHP -.->|client credentials| IDP
    NOT -->|HTTPS, the contact read| IDP
    NOT -.->|client credentials| IDP

    PAY -->|HTTPS, authorise and void| PSP
    SHP -->|HTTPS, book and cancel| CAR

    GW -.->|validate| IDP
    BFF -.->|client credentials| IDP
    CAT -.-> IDP
    ORD -.-> IDP
    INV -.-> IDP
    PAY -.-> IDP

    CAT --> SQL
    ORD --> SQL
    INV --> SQL
    PAY --> SQL
    SHP --> SQL
    NOT --> SQL
    BFF --> SQL

    CAT --> RC
    ORD --> RC
    INV --> RC
    CAT --> RK
    ORD --> RK
    INV --> RK

    CAT <--> MQ
    ORD <--> MQ
    INV <--> MQ
    PAY <--> MQ
    SHP <--> MQ
    NOT <--> MQ
    MQ --> BFF

    CAT --> OTEL
    ORD --> OTEL
    INV --> OTEL
    PAY --> OTEL
    SHP --> OTEL
    NOT --> OTEL
    GW  --> OTEL
    BFF --> OTEL
    OTEL --> GRAF
```

**Which services reach neither Redis instance (§2.2).** Payments, Shipping and
Notifications reach neither: none caches anything, and §8.5's keys belong to
HTTP write commands, which none has. Payments' idempotency is the payment
provider's key and its own rows, Shipping's is the lease its workers take in
SQL, and Notifications' is §9.5's inbox and the unique key on its record.

**The external parties (§2.1).** The context view's carrier is the Shipping
service's, and its label there reads "Shipping carrier"; the chapter says
"Delivery carrier". Its platform node reads "7 domain services + BFF"; the
container view above is the one that draws six.

## E.2 §4: the solution tree and the scaffold

Filled by #904.

## E.3 §5, §7 and §8: aggregates, persistence and caching

This section holds what [§5](05-tactical-ddd.md), [§7](07-persistence.md)
and [§8](08-caching-redis.md) no longer name: which reference service each
sample comes from, and the cases the rules there were drawn from. Each
sub-section is cited by the chapter that lost the fact. The roster of
services itself is [E.1](#e1-2-and-3-the-service-roster-and-the-context-map).

### E.3.1 §5: the Order samples

Every `Service` in §5's samples stands for `Ordering`, the service that owns
the `Order` aggregate. The chapter writes the placeholder and the reference
domain spells the name:

| Where in §5 | The chapter writes | The reference domain has |
|---|---|---|
| §5.2, `OrderId` | `namespace Service.Domain.Orders;` | `namespace Ordering.Domain.Orders;` |
| §5.3, `Money` | `namespace Service.Domain.Common;` | `namespace Ordering.Domain.Common;` |
| §5.4, `Order` | `namespace Service.Domain.Orders;` | `namespace Ordering.Domain.Orders;` |
| §5.4, the comment above the `OrderPlacedDomainEvent` raise | "so the stock owner can reserve stock (§9.6)" | "so Inventory can reserve stock (§9.6)": Inventory is the stock owner |
| §5.5, the events | `namespace Service.Domain.Orders.Events;` | `namespace Ordering.Domain.Orders.Events;` |
| §5.5, the comparison table's Example row | `Common.Contracts.Service.V1.OrderConfirmed` | `Common.Contracts.Ordering.V1.OrderConfirmed` |
| §5.5, the paragraph on shared type names | `Common.Contracts.Service.V1.OrderPlaced` | `Common.Contracts.Ordering.V1.OrderPlaced` |
| §5.6, `IOrderRepository` | `namespace Service.Domain.Orders;` | `namespace Ordering.Domain.Orders;` |

### E.3.2 §7: persistence

The reference domain's names behind the samples and the cases of §7:

| Where in §7 | The chapter writes | The reference domain has |
|---|---|---|
| §7.1, the Compose and CI provisioning sample | `USE [<Service>];`, `[<service>-runtime]`, `[<service>-migrator]`, `$(<Service>RuntimePassword)`, `$(<Service>MigratorPassword)` and `SCHEMA::[<service>]` | `USE [Ordering];`, `ordering-runtime`, `ordering-migrator`, `OrderingRuntimePassword`, `OrderingMigratorPassword` and the schema `ordering`; the connection strings `ConnectionStrings__Ordering` and `ConnectionStrings__OrderingMigrator` stay in the sample as that service's spelling |
| §7.2, the `AddTransactionalOutboxEntities` paragraph | "an `IEntityTypeConfiguration<InboxState>` written in the service's Infrastructure project" | "written in `Ordering.Infrastructure`" |
| §7.2, the mapping excerpt | "`OrderConfiguration`, which holds the whole mapping" | `OrderConfiguration.cs` in `Ordering.Infrastructure/Persistence` holds the whole mapping |
| §7.4, the library's technical tables row | "A service has any of them only if it holds a saga" | Ordering is the only service with any of them, because it holds the only saga ([§9.6](09-messaging.md)) |
| §7.5, the collector port | "an `EfDomainEventCollector` in the service's `Infrastructure/Persistence` folder", in `namespace Service.Infrastructure.Persistence;` | Ordering's is `EfDomainEventCollector` in `Ordering.Infrastructure/Persistence`, in `namespace Ordering.Infrastructure.Persistence;`, over `OrderingDbContext` where the chapter writes `ServiceDbContext` |

The case behind §7.3's pessimistic update stays in the chapter as evidence
and cites this section for its facts. Inventory is the exception to
optimistic concurrency: stock reservation is genuinely contended, because the
same SKU may be reserved by many concurrent orders, and optimistic retry
loops degrade badly under that load. The statement is the `TakeSql` of
`SqlStockLedger` in `Inventory.Infrastructure/Persistence`, against the
`inventory.StockItems` table, and the instant it writes is the `OccurredAt`
of the `StockLevelChanged` that write publishes
([§3.2](03-bounded-contexts.md)). A bare clock reading in both places fails:
the second writer can take an earlier instant than the first, and Catalog's
projection then keeps the older level.

### E.3.3 §8: caching

The reference domain's names behind §8's rules and samples:

| Where in §8 | The chapter writes | The reference domain has |
|---|---|---|
| §8.1, the `ApplicationName` example | "a host deployed as `Service.Api` takes `~Service.Api:*`" | "a host deployed as `Ordering.Api` takes `~Ordering.Api:*`" |
| §8.2, the one-product read | "A real one-product read may not be this handler" | **Catalog's real one-product read is not this handler, and it caches nothing.** `GetProductHandler` ([§6.5](06-cqrs.md)) returns the listing's row, and that row carries Inventory's stock level, which `StockLevelProjection` writes and nothing stages a `Local` invalidation for (§8.4). A cached copy would serve a level no event removes, so the read stays where the listing is: Dapper over the tables, at level 1. |
| §8.4, the sequence diagram | participants "Owning API" and "Consuming service" | participants "Catalog API" and "Ordering" |
| §8.4, the local lane | "The owner's own invalidation goes through the local outbox lane" | "Catalog's own invalidation goes through the local outbox lane, not an inline call after commit" (ADR-018) |
| §8.4, the first-cache rule | "A service that caches nothing stages the `Broker` row alone" | Catalog caches nothing, so its price change stages the `Broker` row alone; the `Local` half arrives with Catalog's first cached read, beside its handler |
| §8.4, the consumer | "a consumer that caches nothing through `HybridCache`", whose one `PriceChanged` handler is a projection | The consumer's side is Ordering's: Ordering caches nothing through `HybridCache`, so its one `PriceChanged` handler is `ProductPriceProjection` ([§6.6](06-cqrs.md)), which updates the price table the write path reads. The first cached projection of Catalog data brings the second handler with it, in `Ordering.Infrastructure` beside the projection |
| §8.4, the invalidator sample | `namespace Service.Infrastructure.Caching;` | `namespace Ordering.Infrastructure.Caching;` |
| §8.5, the Redis claim | "a service's own `Infrastructure.Idempotency` namespace" | `Ordering.Infrastructure.Idempotency`, beside the service that uses it: the obvious home §8.5 rejects, since the code is `Common.Infrastructure.Idempotency` |
| §8.5, the opt-in test | "`IdempotencyOptInTests.cs` in every service's `Application.Tests` project" | Ordering's is `tests/Ordering.Application.Tests/IdempotencyOptInTests.cs` |
| §8.5, the replay test | "A service's `Api.Tests` posts the same `CommandId` twice" | `Catalog.Api.Tests` posts the same `CommandId` twice through a real Redis and asserts one product and one identical response |
| §8.5, the test doubles | "A service's callers, stated through its own `TestAuthHandler`" | Ordering's callers, stated through its `TestAuthHandler` |

## E.4 §6: CQRS and its projections

Filled by #910.

## E.5 §9: messaging and the sagas

Filled by #909.

## E.6 §10 and §11: the gateway routes and the identity realms

Filled by #911.

## E.7 §12: the test strategy

Filled by #905.

## E.8 §13 and §14: observability and local development

This section holds what [§13](13-observability.md) and
[§14](14-local-development.md) no longer state about the reference domain's
services: the value of each `<Service>` placeholder in their samples, the
cases their rules were argued from, and the sample resources that name a
service. Where a chapter's sample writes `<Service>` or `<service>`, the
reference domain's value is Ordering, `ordering`, unless the row says
otherwise.

**The placeholders (§13.3, §13.5, §13.6, §14.1).** The chapters write a
service's name three ways: `<Service>` (also `Service` inside a namespace) is
the cased directory name under `src/Services/`, `<service>` is its lowercase
form, and `{service}` is only ever §8.3's key prefix, the host's
`ApplicationName`.

**The hosts `AddObservability` names (§13.2).** Its code comment
(`src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`) reads
"`Catalog.Api` and `Catalog.Worker` both own `Catalog.*`; a name with no dot
owns itself". In the reference domain `Catalog.Api` exports the `Catalog.Outbox`
meter, and a service's own meters are `Catalog.*` by that rule.

| Sample | What the chapter writes | The reference domain's value |
|---|---|---|
| §13.3, the business-metrics type | `src/Services/<Service>/<Service>.Application/Orders/OrderMetrics.cs`, `namespace <Service>.Application.Orders;`, `factory.Create("<Service>.Orders")` | `src/Services/Ordering/Ordering.Application/Orders/OrderMetrics.cs`, `namespace Ordering.Application.Orders;`, `factory.Create("Ordering.Orders")` |
| §13.3, the projection that records it | `<Service>.Infrastructure/Projections/` | `Ordering.Infrastructure/Projections/` |
| §13.5, the readiness registration | `Add<Service>Infrastructure` in `src/Services/<Service>/<Service>.Infrastructure/DependencyInjection.cs`, `GetConnectionString("<Service>")` | `AddOrderingInfrastructure` in `src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs`, `GetConnectionString("Ordering")` |
| §13.6, the outbox gauges and their registration | `src/Services/<Service>/<Service>.Infrastructure/Observability/`, the same `DependencyInjection.cs` | `src/Services/Ordering/Ordering.Infrastructure/Observability/`, Ordering's `DependencyInjection.cs` |
| §13.6, where `OrderMetrics` lives and its registration test | `<Service>.Application`, `tests/<Service>.Api.Tests/MetricsRegistrationTests.cs` | `Ordering.Application`, `tests/Ordering.Api.Tests/MetricsRegistrationTests.cs` |
| §14.1, a service's migrator and API unit | `<service>-migrator`, `<service>-api`, `<Service>.Migrator`, `<Service>.Api`, `<SERVICE>_CONNECTION` | `deploy/compose/services/ordering.yml`, the unit below |
| §14.1, running a host under the override | `<Service>.Api`, `ConnectionStrings__<Service>`, `<service>-svc` | `Ordering.Api`, `ConnectionStrings__Ordering`, `ordering-svc` |
| §14.1, the override's entries | `<service>-migrator`, `<service>-api` | `ordering-migrator`, `ordering-api` |
| §14.2, the AppHost sample | `serviceDb`, `serviceMigrator`, `service`, `<Service>Migrator` | `orderingDb`, `orderingMigrator`, `ordering`, `OrderingMigrator`; the Catalog resources the chapter omits are below |

**Cases §13 was argued from.**

- *Where the business metrics live (§13.3).* `OrderMetrics` records domain
  quantities, so it cannot sit in `Common.Application`. Its only call site is
  `OrderSummaryProjection`, which is Infrastructure, so Infrastructure would
  also compile; it belongs in `Ordering.Application` anyway, because the type
  is a statement about the business vocabulary (placed, cancelled, fulfilled).
- *The never-log vocabulary (§13.4).* `pin` is absent from `SensitiveKeys`
  because matching is by substring and `Shipping` contains it. The remark in
  `src/BuildingBlocks/Common.Web/SensitiveKeys.cs` names the service.
- *Where the redaction test lives (§13.4).* A building block asserted in
  Ordering's suite is one that moves house if Ordering ever does, so the test
  is in `Common.Web.Tests`.
- *The retention window of a log (§13.4).* Notifications'
  `Jurisdiction:LogRetention` is the window its `NotificationLog` rows are kept
  as evidence, which its own purge applies; the chapter keeps that sentence as
  its own evidence.
- *Hosts with no public API (§13.5, §13.6).* Shipping and Notifications both
  ship a migrator and register a SQL check, and neither has a public API, so
  `RequestMetrics` would never exist on either. The BFF's readiness set is its
  own SQL and the bus, and Catalog's hop is kept out of it.
- *Work outside a consumer (§13.6, queue backlog).* Shipping's fulfilment and
  tracking passes keep their work in the service's own tables, so the backlog
  alert cannot see them; §15.3 names the gauge that does.
- *Replicas (§13.6, outbox gauges).* §15.3's Ordering chart runs three
  replicas, so `sum` over the table-wide gauges reports three times the
  backlog.
- *The scaffold's template (§13.6).* Catalog is what §4.5's scaffold renders,
  so it registers the outbox gauges and every rendered publisher inherits
  them.
- *A read model with no outbox row (§13.7).* Ordering's
  `ordering.ProductPrices` is fed by another service's contract and never
  touches the outbox, so `projection.lag` is empty for it; Ordering's own
  producer of that signal is `OrderSummaryProjection` (§6.6), which every
  placed order reaches.
- *Event end-to-end in the load run (§13.7).* The consumer that records
  `messaging.delivery.lag` handles Catalog's product events, which neither
  scenario produces.

**The domain alerts and their runbooks (§13.6, §13.9).** The rows stay in the
chapter, because `deploy/observability/check.py` reads each runbook name there;
this table says whose signal each reads.

| Alert (§13.6) | Service | Signal |
|---|---|---|
| Unattributed order | Ordering, read by the BFF | `bff.orders.unattributed` |
| Address read refused | Shipping, reading Ordering | `shipping.address.refused` |
| Contact read refused | Notifications | `notifications.contact.refused` |
| Erasure requests overdue | Privacy | `privacy.erasure.overdue`, with `Privacy:CompletionSlo` |
| Unscanned shipments | Shipping | `shipping.shipments.unscanned` |
| Saga age | Ordering | the saga instances in `ordering.OrderFulfilmentStates` |
| Orders awaiting review | Ordering | the rows in `ordering.OrderReviews` |

`shipping.shipments.overdue` carries no rule yet, by decision: a threshold on
how long a due shipment has waited wants a baseline under load, and none
exists until §13.7's load run is scheduled or declined
([#434](https://github.com/alexander-shamray/blueprint-backend/issues/434)).
It, `shipping.shipments.waiting` and `shipping.carrier.unavailable` are read by
`queue-backlog.md`'s procedure for a worker whose wait is outside a consumer.

**Cases §14 was argued from.**

- *The unit with no client credentials (§14.1).* Ordering's API carries the
  authority, to validate inbound tokens, and no `Identity__Client__*`:
  Ordering calls no peer synchronously, because prices come from a local
  projection (§6.4) and the rest goes over the broker.
- *The mail relay (§14.1).* Mailpit runs in Notifications' own unit beside the
  worker that submits to it, which reaches it as `mailpit:1025`; both its ports
  bind loopback alone.
- *The exported key (§14.1).* `AddOrderingInfrastructure` reads
  `ConnectionStrings:Ordering` and `AddSqlServer` throws without it, so
  exporting Catalog's key for an Ordering host cannot start the process; the
  migrator takes `ConnectionStrings__OrderingMigrator`.
- *The pinned ports (§14.1).* Ordering's `appsettings.json` pins 8080 and 8081
  (ADR-052), and 8080 on the host is Keycloak's, so a host run moves Ordering
  to 5101 and 8082. `deploy/compose/README.md` gives Catalog's pair beside
  Ordering's.
- *The seeders (§14.3).* `CatalogSeeder` and `InventorySeeder` write what
  their services' own commands would have: each product with the
  `ProductPublished` outbox row `PublishProduct` stages, each count with the
  `StockLevelChanged` row `SetOnHand` stages, so Ordering's prices, the BFF's
  names and Catalog's stock levels fill through the platform's delivery.
  Inventory is seeded directly because stock reaches it by the admin path and
  from no event. The gate that keeps a seeder out of production is in
  `src/Services/Catalog/Catalog.Migrator/MigratorHost.cs`.

**The Ordering unit (§14.1).** The chapter's sample is this file's shape,
`deploy/compose/services/ordering.yml`.

```yaml
ordering-migrator:
  build:
    context: ../../..
    dockerfile: src/Services/Ordering/Ordering.Migrator/Dockerfile
  read_only: true
  tmpfs: [ /tmp ]
  environment:
    ConnectionStrings__OrderingMigrator: "${ORDERING_MIGRATOR_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
    DOTNET_ENVIRONMENT: Development
  depends_on:
    sql: { condition: service_healthy }
  restart: "no"

ordering-api:
  build:
    context: ../../..
    dockerfile: src/Services/Ordering/Ordering.Api/Dockerfile
  read_only: true
  tmpfs: [ /tmp ]
  environment:
    ASPNETCORE_ENVIRONMENT: Development
    ConnectionStrings__Ordering: "${ORDERING_CONNECTION:-Server=sql;Database=Ordering;User Id=sa;Password=${SQL_PASSWORD:-Local_Dev_Pa55w0rd!};TrustServerCertificate=True}"
    ConnectionStrings__RabbitMq: "amqp://ordering-svc:local-dev-ordering@rabbitmq:5672"
    Identity__Authority: "http://keycloak:8080/realms/commerce"
    ConnectionStrings__RedisCache: "redis-cache:6379,user=ordering-svc,password=local-dev-ordering"
    ConnectionStrings__RedisCoordination: "redis-coordination:6379,user=ordering-svc,password=local-dev-ordering"
  ports: [ "127.0.0.1:5101:8080" ]
  healthcheck:
    test: [ "CMD", "dotnet", "Ordering.Api.dll", "--probe" ]
  depends_on:
    ordering-migrator: { condition: service_completed_successfully }
    rabbitmq: { condition: service_healthy }
    keycloak: { condition: service_healthy }
    redis-cache: { condition: service_healthy }
    redis-coordination: { condition: service_healthy }
```

**Running the Ordering host under the override (§14.1).**

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://ordering-svc:local-dev-ordering@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
export ConnectionStrings__RedisCache='localhost:6379,user=ordering-svc,password=local-dev-ordering'
export ConnectionStrings__RedisCoordination='localhost:6380,user=ordering-svc,password=local-dev-ordering'
# Not a key the host throws without, but a bind it fails: Ordering's
# appsettings.json pins 8080 and 8081 (ADR-052), and 8080 on the host is
# Keycloak's. deploy/compose/README.md gives Catalog's pair beside these.
export Kestrel__Endpoints__Rest__Url='http://localhost:5101'
export Kestrel__Endpoints__Grpc__Url='http://localhost:8082'
```

**The override's entries for Ordering (§14.1).**

```yaml
services:
  ordering-migrator:
    profiles: [ "excluded" ]
  ordering-api:
    profiles: [ "excluded" ]
```

**The AppHost's reference resources (§14.2).** The chapter's AppHost sample
writes one service, `service`; the reference domain's AppHost runs Ordering and
Catalog, with these resources in place of the sample's. The RabbitMQ comment
names the saga service:

```csharp
// (ADR-021). Ordering's saga schedules through the delayed message exchange,
// which is a community plugin no official image carries — so this line brings
// up a broker that takes UseDelayedMessageScheduler, connects, reports
// healthy, and then hangs on the first saga schedule. §14.1 builds
// deploy/compose/rabbitmq for exactly that reason.
//
// It is left as the stock call because Aspire is not adopted (ADR-011) and a
// sample nobody compiles is the wrong place to invent an image-build API.
// **Adopting Aspire means closing this first**: point the resource at the
// same Dockerfile, or run Ordering against Compose. Catalog's test fixture
// may stay on the base tag and says why — it schedules nothing — and this
// AppHost runs Ordering, so it does not have that excuse.
```

The databases, `bffDb` as the chapter has it:

```csharp
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb = sql.AddDatabase("Catalog");
```

The migrators, `bffMigrator` as the chapter has it:

```csharp
var orderingMigrator = builder
    .AddProject<Projects.Ordering_Migrator>("ordering-migrator")
    .WithReference(orderingDb, connectionName: "OrderingMigrator")
    .WaitFor(sql);

var catalogMigrator = builder
    .AddProject<Projects.Catalog_Migrator>("catalog-migrator")
    .WithReference(catalogDb, connectionName: "CatalogMigrator")
    .WaitFor(sql);
```

The two services and the comments between them. The gateway references both
`ordering` and `catalog`, and the BFF references `catalog`:

```csharp
var ordering = WithPlatformIdentity(
    builder
        .AddProject<Projects.Ordering_Api>("ordering-api")
        .WithReference(orderingDb)
        .WaitFor(orderingDb)
        .WithReference(cache)          // → ConnectionStrings:RedisCache
        .WithReference(coordination)   // → ConnectionStrings:RedisCoordination
        .WithReference(mq)
        .WaitFor(mq)
        // Gate on the migrator completing, not merely starting — the Compose
        // equivalent is service_completed_successfully (§14.1).
        .WaitForCompletion(orderingMigrator)
        // Pinned ports, moved as Catalog's are below; 8082 because Catalog's
        // h2c listener holds 8081 (ADR-052).
        .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5101")
        .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8082")
        .WithHttpHealthCheck("/health/ready"));   // authority only — no peer calls

// Catalog and Ordering are the resources here whose ports Aspire does NOT get
// to choose. Each appsettings.json declares Kestrel:Endpoints — 8080 for REST
// and 8081 for its gRPC surface, because a cleartext port cannot serve HTTP/1.1
// and h2c at once — and declaring that section suppresses ASPNETCORE_URLS and
// ASPNETCORE_HTTP_PORTS entirely, which is how Aspire assigns a port. So each
// binds 8080 whatever this host allocates, and 8080 is what AddKeycloak took
// above: they fight for it unless each is moved.
//
// Move Catalog's, with the same configuration keys the compose README uses for
// `dotnet run` — a higher provider is the only thing that overrides that file:
//
//     .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5102")
//     .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8081")
//
// And note what WithReference cannot do here: the BFF's hop address is the
// literal http://catalog-api:8081 (§9.7, §15.4), so a reference does not
// re-point it. Aspire runs the BFF against whatever answers on that name.
//
// No seed runs under this sample, whose migrators set no Seed__Enabled: §14.3
// delivers seeded events only through §14.1's start order, and Catalog here
// waits on neither Ordering nor the BFF, so a seeded event would go unheard.
var catalog = WithPlatformIdentity(
    builder
        .AddProject<Projects.Catalog_Api>("catalog-api")
        .WithReference(catalogDb)
        .WaitFor(catalogDb)
        .WithReference(cache)
        .WithReference(coordination)
        .WithReference(mq)
        .WaitForCompletion(catalogMigrator)
        .WithEnvironment("Kestrel__Endpoints__Rest__Url", "http://localhost:5102")
        .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://localhost:8081")
        .WithHttpHealthCheck("/health/ready"));
```

## E.9 §15: CI/CD and deployment

Filled by #903.

---

[← Appendix D](appendix-d-type-inventory.md) · [Index](README.md)
