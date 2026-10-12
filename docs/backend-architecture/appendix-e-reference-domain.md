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

Filled by #907.

## E.2 §4: the solution tree and the scaffold

Filled by #904.

## E.3 §5, §7 and §8: aggregates, persistence and caching

Filled by #908.

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
