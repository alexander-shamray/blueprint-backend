# Appendix E — The reference domain

This appendix owns every fact about the reference domain that a chapter no
longer states: the services and what each is, their contracts, queues, ports,
realms, Compose units and CI filters where no other file holds them, and the
worked examples. A chapter states a rule abstractly and cites a section here
by link, never a value; an adopter replaces this appendix wholesale
([ADR-096](adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md),
[ADR-062](adr/ADR-062-the-domain-is-a-reference-implementation.md)).
The [domain-name gate](../../.github/domain-name-gate/README.md) fails a chapter
block that names a service without ADR-096's marker.

Each section below is a skeleton, one per chapter pass, so that a pass fills
the section it cites, replacing its "Filled by" line, and creates none.

## E.1 §2 and §3: the service roster and the context map

This section holds what [§2](02-architecture-at-a-glance.md) and
[§3](03-bounded-contexts.md) no longer state about the reference domain's
services. [§3.2](03-bounded-contexts.md)'s table of what each service owns,
publishes, consumes and accepts is [E.1.1](#e11-32-the-service-responsibilities-table)'s.

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

**The container view (§2.2).** A node per service; the chapter draws the
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
        NAPI[Notifications API]
        PRV[Privacy API]
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
    GW --> PRV
    GW --> BFF

    BFF -->|gRPC, pricing| CAT
    BFF -->|gRPC, device registration| NAPI

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
    PRV -.-> IDP
    NAPI -.-> IDP

    CAT --> SQL
    ORD --> SQL
    INV --> SQL
    PAY --> SQL
    SHP --> SQL
    NOT --> SQL
    NAPI --> SQL
    PRV --> SQL
    BFF --> SQL

    CAT --> RC
    ORD --> RC
    INV --> RC
    CAT --> RK
    ORD --> RK
    INV --> RK
    PRV --> RK

    CAT <--> MQ
    ORD <--> MQ
    INV <--> MQ
    PAY <--> MQ
    SHP <--> MQ
    NOT <--> MQ
    PRV <--> MQ
    MQ --> BFF

    CAT --> OTEL
    ORD --> OTEL
    INV --> OTEL
    PAY --> OTEL
    SHP --> OTEL
    NOT --> OTEL
    NAPI --> OTEL
    PRV --> OTEL
    GW  --> OTEL
    BFF --> OTEL
    OTEL --> GRAF
```

**Which services reach neither Redis instance (§2.2).** Payments, Shipping and
Notifications reach neither: none caches anything, and §8.5's keys belong to
HTTP write commands, which none has. Payments' idempotency is the payment
provider's key and its own rows, Shipping's is the lease its workers take in
SQL, and Notifications' is §9.5's inbox and the unique key on its record.

Privacy is the fourth that reaches Redis, and only the coordination
instance: its reissue endpoint is an HTTP write command, so §8.5's key
belongs there, and it caches nothing. The host still registers both
connections and probes both in readiness, because `AddRedisConnections` reads
both (§8.2), so the cache instance is registered and carries nothing, and has
no edge. Its erasure locks are SQL application locks (`sp_getapplock`), not
Redis ones; E.6.2 owns their refusal.

**The external parties (§2.1).** The context view's carrier is the Shipping
service's, and its label there reads "Shipping carrier"; the chapter says
"Delivery carrier". Its platform node reads "7 domain services + BFF"; the
container view above is the one that draws all seven.

### E.1.1 §3.2: the service responsibilities table

[§3.2](03-bounded-contexts.md) states the rule: each service is described
by what it owns, publishes, consumes and accepts, and the table closes in
both directions.
This table is the reference domain's, and the
[messaging gate](../../.github/messaging-gate/README.md) reads it here and
holds every row to what its host's code publishes, consumes and accepts.

| Service | Owns | Publishes (events) | Consumes (events) | Accepts (commands) |
|---|---|---|---|---|
| **Catalog** | Product, its Price | `ProductPublished`, `PriceChanged`, `ProductDiscontinued` | `StockLevelChanged` | — |
| **Ordering** | Order, OrderLine, the fulfilment saga | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled` | `OrderPlaced` (its own — the saga starts on it), `OrderCancelled` (its own — the saga stops on it), `OrderConfirmed` (its own — the saga waits on it), `ProductPublished`, `PriceChanged`, `ProductDiscontinued`, `StockReserved`, `StockReservationFailed`, `StockReleased`, `PaymentAuthorised`, `PaymentDeclined`, `ShipmentDispatched`, `PersonalDataDeleteRequested` | `CancelOrder`, `ConfirmOrder`, `MarkOrderShipped`, `FlagOrderForReview` |
| **Inventory** | StockItem, Reservation | `StockReserved`, `StockReservationFailed`, `StockReleased`, `StockLevelChanged` | `OrderCancelled`, `ShipmentDispatched` | `ReserveStock`, `ReleaseStock` |
| **Payments** | PaymentIntent, Refund | `PaymentAuthorised`, `PaymentDeclined`, `PaymentRefunded` | `OrderPlaced`, `OrderCancelled`, `PersonalDataDeleteRequested` | `AuthorisePayment` |
| **Shipping** | Shipment, TrackingEvent | `ShipmentDispatched`, `ShipmentDelivered` | `OrderConfirmed`, `OrderCancelled`, `PersonalDataDeleteRequested` | — |
| **Notifications** | NotificationLog and its push deliveries, the device rows, the consent journal ([ADR-120](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)) | — | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `PaymentDeclined`, `PaymentRefunded`, `ShipmentDispatched`, `ShipmentDelivered`, `PersonalDataDeleteRequested` | — |
| **Privacy** | ErasureRequest | `PersonalDataDeleteRequested` | — | `PersonalDataDeleteCompleted` |
| **Web.Bff** — a host, not a service ([§4.1](04-solution-structure.md)) | The buyer's order projection, a read model of other services' facts ([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)), and a customer's saved address, the one fact it owns ([ADR-107](adr/ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md)) | — | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `PaymentAuthorised`, `PaymentRefunded`, `ShipmentDispatched`, `ShipmentDelivered`, `ProductPublished`, `PersonalDataDeleteRequested` | — |

## E.2 §4: the solution tree and the scaffold

This section holds what [§4](04-solution-structure.md) no longer states: the
services that have each host shape, the worked samples of its gates and its
composition root, and the cases it compares a rule against. The chapter owns
the rules; each sample below is one service's, in the reference domain, and
an adopter replaces it with their own.

### E.2.1 The services and their host shapes

`src/Services/` is the roster, one directory per service, and
`deploy/compose/` holds each service's Compose unit; this table adds only
what neither says, which host shape each service has. Each service's tests
are `<Service>.Domain.Tests`, `<Service>.Application.Tests` and
`<Service>.Api.Tests` (`<Service>.Worker.Tests` for a worker, both for a
service with both hosts, and no `<Service>.Domain.Tests` for a service with
no Domain project), with a `<Service>.TestSupport`, under `tests/`.

| Service | Host shape | Projects under `src/Services/<Service>/` |
|---|---|---|
| Catalog | API host | Domain, Application, Infrastructure, Migrator, Api |
| Ordering | API host | Domain, Application, Infrastructure, Migrator, Api |
| Inventory | API host | Domain, Application, Infrastructure, Migrator, Api |
| Payments | API host | Domain, Application, Infrastructure, Migrator, Api |
| Privacy | API host | Domain, Application, Infrastructure, Migrator, Api |
| Shipping | worker | Domain, Application, Infrastructure, Migrator, Worker |
| Notifications | worker and API host, no Domain | Application, Infrastructure, Migrator, Worker, Api |

Ordering's tree is the one §4.1 draws generically: its Application project
holds a `DependencyInjection.cs` with `AddOrderingApplication()`, its
Infrastructure project one with `AddOrderingInfrastructure(config)`, and
`Ordering.Api/Program.cs` is its only composition root.

Shipping and Notifications expose no public API but still own schemas:
`Shipment` and `TrackingEvent`, and `NotificationLog`, respectively
([§3.2](03-bounded-contexts.md)). Notifications' API host shares the worker's
database and migrator and serves ADR-117's device registration and ADR-120's
preferences, which are the BFF's alone to call, on a port no gateway route
reaches
([ADR-118](adr/ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)).
`tools/new-service` refuses Shipping without `--worker` and Notifications
without `--pure-consumer`, so that it renders Shipping only in the shape §4.1
gives it and Notifications only as the render its tree was built on: a pure
consumer, beside which the API host was added in a shape the scaffold has no
mode for.

### E.2.2 The domain gate

Ordering's, in `tests/Ordering.Domain.Tests/ArchitectureTests.cs`, enforces
§4.2's `*.Domain` row as an exact allow-list:

```csharp
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections is the records' generated equality; System.Linq is the value objects'
        // letter scans and Order's Aggregate over its lines.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Order).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
```

### E.2.3 The composition-root gate

Ordering's, in `tests/Ordering.Api.Tests/ArchitectureTests.cs`, judges the
assembly whole and subtracts the root from the failures afterwards, where
full names are available to subtract it by and there is no candidate set to
be narrow:

```csharp
    private static readonly string[] Forbidden =
    [
        "Ordering.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>Program or its global-namespace generated helpers, the one exemption §4.2 grants.</summary>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Every banned package, not the Infrastructure namespace alone: DbContext and the like arrive transitively.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .ShouldNot().HaveDependencyOnAny(Forbidden)
            .GetResult();

        string[] leaked = [.. (result.FailingTypeNames ?? []).Where(name => !IsCompositionRoot(name))];

        leaked.ShouldBeEmpty($"leaked: {string.Join(", ", leaked)}");
    }
```

### E.2.4 The MassTransit gate

Ordering's, in `tests/Ordering.Application.Tests/ArchitectureTests.cs`,
holds the boundary [§9.3](09-messaging.md) states in prose:

```csharp
    [Fact]
    public void Application_and_domain_do_not_reference_masstransit()
    {
        // §9.3's must-not list, whose one exemption is a saga's receive endpoint and its outbox (ADR-032).
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Order).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful
                .ShouldBeTrue(assembly.GetName().Name);
        }
    }
```

### E.2.5 The cross-service gate

Ordering's, in `tests/Ordering.Api.Tests/ArchitectureTests.cs`, is the list
of building blocks, the service's own projects read off the five
assemblies, and the helper its test calls once for each of them; the
excerpt leaves out the lines between the first two:

```csharp
    /// <summary>§4.1's building blocks by name, because Common.TestSupport is named like one and is not.</summary>
    private static readonly string[] BuildingBlocks =
        ["Common.Application", "Common.Contracts", "Common.Domain", "Common.Infrastructure", "Common.Web"];

    /// <summary>The service's own projects by name, never its prefix, which Ordering.TestSupport shares.</summary>
    private static readonly string[] OwnProjects = [.. ServiceAssemblies.Select(assembly => assembly.GetName().Name!)];

    private static void ShouldStayInsideThisService(string subject, AssemblyName[] references)
    {
        string[] foreign =
        [
            .. references
                .Where(IsFirstParty)
                .Select(reference => reference.Name!)
                .Where(name => !BuildingBlocks.Contains(name) && !OwnProjects.Contains(name))
                .Order()
        ];

        foreign.ShouldBeEmpty($"{subject} reaches across a service boundary: {string.Join(", ", foreign)}");
    }
```

### E.2.6 The composition root

Ordering's three, each with the comments that name the line's owner.

`AddOrderingApplication`, in
`src/Services/Ordering/Ordering.Application/DependencyInjection.cs`:

```csharp
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves, as E.2.6's sample has it (§7.5).
        services.AddDomainEventDispatcher();

        // §9.3's allow-list, explicit so that what this service publishes is a decision, not a scan's finding.
        services.AddScoped<IIntegrationEventMapper, OrderingIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // §13.3's business instruments; Infrastructure's projection is the only caller (§6.6).
        services.AddSingleton<OrderMetrics>();

        // Registration order is pipeline order; idempotency sits inside validation and outside the transaction (§6.3).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // Scoped, as a command is; only open generics inject it, so a missing one fails the first command.
        services.AddScoped<IdempotencyContext>();

        // E.2.6's sample line, anchored on a validator: losing the last one fails the build, not silently.
        services.AddValidatorsFromAssemblyContaining<PlaceOrderValidator>();
        return services;
    }
```

Most comments name their line's owner, and three reasons live here alone.
`TimeProvider.System` is registered here so that Application's registration
stands on its own, rather than on whatever a host's other building blocks happen
to `TryAdd`. `AddDispatcher` and `AddDomainEventDispatcher` stand in for
`AddScoped` lines this assembly cannot write, because `Dispatcher`,
`DomainEventDispatcher` and `ProjectionRegistry` are internal to
`Common.Application` (§6.2, §7.5). And the mapper is not an open generic, so
§6.2's scan cannot find it; `DomainEventDispatcher` injects it, so a missing
line fails `ValidateOnBuild` rather than failing silently.

`AddOrderingInfrastructure`, in
`src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs`:

```csharp
    public static IServiceCollection AddOrderingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ADR-079, before any connection below is opened.
        services.AddTransportSecurity();

        // §7.1's runtime identity, no DDL; EnableRetryOnFailure makes §6.3's CreateExecutionStrategy a real retry.
        services.AddDbContext<OrderingDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Ordering"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. One instance, not AddScoped<DbContext, OrderingDbContext>(), which
        // would commit the inbox row in a second context's own transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

        // Each layer scans itself (§6.2); scanning only Application would skip this layer's projections and mappers.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3
        services.AddScoped<IOrderRepository, OrderRepository>();             // §5.6

        // §6.4's price port, over the local projection, so the write transaction never waits on Catalog.
        services.AddScoped<IProductPriceReader, ProjectedPriceReader>();

        // §8.5's durable half, in EfUnitOfWork's transaction through the alias above. A missing line fails the
        // first command, not startup: ValidateOnBuild never constructs TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §7.5's two halves, scoped because the context is.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // Values, since Common.Infrastructure is every service's; one local, so no two tables name different schemas.
        const string schema = "ordering";
        services.AddSingleton(new OutboxTable(schema));
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // §9.4's, §9.5's and §8.5's retention windows, registered rather than const so the service can change them.
        services.AddSingleton(new RetentionPolicy());

        // §9.4's persisted type names; the source is separate so a test host can add its own assembly.
        // The map is lazy, so MessageTypeMapValidator is what fails the host, not the first message, on a duplicate.
        services.AddSingleton(
            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(Order).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // §9.4's payload format. A value object on a domain event needs a converter here: most deserialise to
        // their default rather than failing, which §12.4's round trip catches.
        services.AddSingleton<JsonConverter, MoneyJsonConverter>();
        services.AddSingleton<JsonConverter, AddressJsonConverter>();
        services.AddSingleton<JsonConverter, PaymentReferenceJsonConverter>();
        services.AddSingleton<JsonConverter, TrackingNumberJsonConverter>();
        services.AddSingleton<OutboxJson>();

        // §13.3's messaging instruments.
        services.AddSingleton<MessagingMetrics>();

        // §13.6's outbox gauges. OutboxStats reads the runtime key's data plane (§7.1), and runs in gauge callbacks,
        // so it gets its own bounded connect timeout, which no query inherits.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Ordering"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp =>
            new OutboxStats(new SqlConnectionFactory(metricsConnectionString), sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Constructs the metrics singletons at start, before the bus, so they exist for the first message (§13.6).
        services.AddHostedService<MetricsInitialiser>();

        // §8's two connections, read eagerly, so a missing key stops the host.
        services.AddRedisConnections(configuration);

        // The bus (§9); AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // §9.4's poll loop. The generic overload records the ImplementationType §12.4's fixture removes it by.
        // After the bus, since hosted services stop in reverse and the dispatcher drains into a live transport.
        services.AddHostedService<OutboxDispatcher>();

        // §9.4's, §9.5's and §8.5's retention. Last, so first stopped: an interrupted purge loses nothing.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side, singleton as E.2.6's sample has it, on the runtime key rather than the migrator's (§7.1).
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Ordering")!));

        // Readiness lives here, not in Common.Web, because it needs the connection strings (§13.5). Both Redis
        // instances, since AbortOnConnectFail is false and §8.1 gives them different servers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Cache)!,
                name: "redis-cache",
                tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Coordination)!,
                name: "redis-coordination",
                tags: ["ready"]);

        return services;
    }
```

Three reasons live here alone, and so do three absences.
`SqlConnectionFactory` is registered as an instance because it takes the
connection string, so a type registration leaves the container no constructor
to satisfy and `ValidateOnBuild` refuses the host; a singleton is safe because
the connections it hands out are the caller's to dispose.
`MessageTypeMapValidator` is registered before the dispatcher because hosted
services start in order. `Money` has a private constructor, so without its
converter it deserialises to a zero amount and a null currency and nothing
says so (§9.4).

The absences are registrations made elsewhere. `ICurrentUser` and
`AddHttpContextAccessor` belong to `AddCommonWebDefaults` (§11.4, §13.2):
neither names a service, and the implementation reads `IHttpContextAccessor`,
which arrives with a `FrameworkReference` that among the building blocks only
`Common.Web` has. `IIdempotencyStore` arrives with `AddRedisConnections`, which
registers `RedisIdempotencyStore` (§8.5), because a service either has Redis or
does not (§8.2). And there is no `ITokenCache`, `ClientCredentialsHandler` or
`ServiceIdentityOptions`, because Ordering makes no synchronous outbound call
and so holds no outbound identity of its own (§9.7, §11.5).

And `src/Services/Ordering/Ordering.Api/Program.cs`, from its builder on:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start on an unsatisfiable dependency or a captured scope, rather than on the first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddOrderingApplication();       // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);   // §4.2, §7.1

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddCommonOpenApi();

// ADR-052's server half; no interceptor, as the only caller-supplied value is parsed before the dispatcher.
builder.Services.AddGrpc();

// RequirePermission, so the claim type is PermissionClaim.Type's alone (§11.4). No orders:admin policy: that string
// is a claim CancelOrderHandler checks against a loaded aggregate.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything, so every response carries it, the handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs
app.UseRequestTimeouts();         // §9.7 — below the exception handler, which would answer 499

// §10.5's error shape for the bodiless challenge and forbid the middleware below writes.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

app.MapOrderEndpoints();          // §11.4 — the group fails closed

// ADR-052, on the Http2 endpoint appsettings.json declares; [Authorize] travels on the service class.
app.MapGrpcService<DeliveryAddressService>().RetrySafe(RetrySafety.ReadOnly);   // ADR-052 — Get reads one address

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory cannot see (§12.4).
public partial class Program;
```

Ordering's permission policies are registered here rather than inside either
helper, because Application knows nothing about HTTP and `Common.Web` must not
know Ordering's names; a policy an endpoint names and nothing registers throws
on the first request that reaches it, never at startup. A constant names each
one, so the compiler compares the name written here with the one at the
endpoint. `public partial class Program` is one line rather than
`InternalsVisibleTo` because it does not have to name the assemblies that
consume it, `Ordering.TestSupport` and `Ordering.Api.Tests` among them, each of
which `InternalsVisibleTo` would have to list.

### E.2.7 The cases the chapter compares against

- **A gRPC service in its own namespace.** Ordering's `DeliveryAddressService`
  is in `.Grpc` (ADR-052), outside `.Endpoints`, which is why a namespace
  selector would miss it.
- **The callees of the BFF's hops.** The stubs that `Web.Bff.TestSupport`
  holds are Catalog's, with the server half of `pricing.proto`, and
  Notifications', with the server halves of `devices.proto` and
  `preferences.proto`; the
  `PricingContract` is linked into `Catalog.Api.Tests`.
- **A bound only one service enforces.** Catalog's id ceiling, which
  `GetPricesValidator` holds, is the case to compare against
  [ADR-045](adr/ADR-045-the-checkout-quote-takes-quantities.md)'s shared
  `OrderLimits`, which the BFF's quote and Ordering's command both enforce.
- **A host with no synchronous outbound call.** Ordering makes none, so it
  holds no `ITokenCache`, `ClientCredentialsHandler` or
  `ServiceIdentityOptions` (§9.7, §11.5).
- **A readiness check the BFF leaves out.** The BFF's readiness set does
  not include Catalog's hop (§13.5).

### E.2.8 The scaffold template

`tools/new-service` has no template directory. It reads
`src/Services/Catalog` at run time, so Catalog is the template: its wiring
is copied and its domain is not. Catalog's `Product`, its command, its
query and its endpoints are excluded by name, and any file under
`src/Services/Catalog` the script cannot classify as template or slice
stops the run. The script renames every casing of `Catalog` after it
applies its patches, which is why a gate that named `Catalog` in a list
would reach the new service with the name replaced.

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
| §8.2, the one-product read | "A real one-product read may not be this handler" | **Catalog's real one-product read is not this handler, and it caches through `IProductReadCache`.** `GetProductHandler` ([§6.5](06-cqrs.md)) returns the listing's row, still Dapper at level 1, read through `HybridCache` keyed `product:{id}:v1` and tagged `product:{id}`. The row carries Inventory's stock level, which `StockLevelProjection` writes, so that projection drops the key after its upsert; the price and the withdrawal are dropped by `ProductPriceCacheInvalidator` and `ProductWithdrawalCacheInvalidator`, `Local` rows staged for `PriceChangedDomainEvent` and `ProductDiscontinuedDomainEvent` (§8.4). A miss is never stored (the load throws on it, which `HybridCache` does not cache, and the adapter turns that back into no row), so an id anyone can send leaves nothing behind. Another replica's level 1 copy lives until `CacheDefaults.LocalCacheExpiration`, which is the staleness this read accepts. The listing stays uncached |
| §8.4, the sequence diagram | participants "Owning API" and "Consuming service" | participants "Catalog API" and "Ordering" |
| §8.4, the local lane | "The owner's own invalidation goes through the local outbox lane" | "Catalog's own invalidation goes through the local outbox lane, not an inline call after commit" (ADR-018) |
| §8.4, the first-cache rule | "A service that caches nothing stages the `Broker` row alone" | Catalog's price change stages the `Broker` row and, since the one-product read is cached, a `Local` row for `PriceChangedDomainEvent`, as does a withdrawal; the `Local` half arrived with that read, beside its handler. A service that still caches nothing stages the `Broker` row alone |
| §8.4, the consumer | "a consumer that caches nothing through `HybridCache`", whose one `PriceChanged` handler is a projection | The consumer's side is Ordering's: Ordering caches nothing through `HybridCache`, so its one `PriceChanged` handler is `ProductPriceProjection` ([§6.6](06-cqrs.md)), which updates the price table the write path reads. The first cached projection of Catalog data brings the second handler with it, in `Ordering.Infrastructure` beside the projection |
| §8.4, the invalidator sample | `namespace Service.Infrastructure.Caching;` | `namespace Ordering.Infrastructure.Caching;` |
| §8.5, the Redis claim | "a service's own `Infrastructure.Idempotency` namespace" | `Ordering.Infrastructure.Idempotency`, beside the service that uses it: the obvious home §8.5 rejects, since the code is `Common.Infrastructure.Idempotency` |
| §8.5, the opt-in test | "`IdempotencyOptInTests.cs` in every service's `Application.Tests` project" | Ordering's is `tests/Ordering.Application.Tests/IdempotencyOptInTests.cs` |
| §8.5, the replay test | "A service's `Api.Tests` posts the same `CommandId` twice" | `Catalog.Api.Tests` posts the same `CommandId` twice through a real Redis and asserts one product and one identical response |
| §8.5, the test doubles | "A service's callers, stated through its own `TestAuthHandler`" | Ordering's callers, stated through its `TestAuthHandler` |

## E.4 §6: CQRS and its projections

### E.4.1 The level each service reads at

[§6.1](06-cqrs.md)'s two levels are used as follows. Catalog, Inventory,
Payments and Shipping read at level 1. Ordering is the service that escalates
to level 2, and [E.4.5](#e45-the-ordering-escalation) is its case. The one
level 2 read outside Ordering is the stock level a Catalog product read joins:
a local projection, `StockLevelProjection` in
`Catalog.Infrastructure/Projections/`, of Inventory's stock events
([§3.2](03-bounded-contexts.md)). Payments' order record and Shipping's
`DeliveryAddresses` are not read models that serve a query, so they are not
level 2.

### E.4.2 The registration scan and its test

Every service's two registration methods call `AddPluggableFrom` on their own
assembly: `<Service>.Application/DependencyInjection.cs` and its
`Infrastructure` sibling. Ordering's are `Ordering.Application/DependencyInjection.cs` and
`Ordering.Infrastructure/DependencyInjection.cs`.

The registration test of [§6.2](06-cqrs.md) is Ordering's,
`tests/Ordering.Api.Tests/RegistrationTests.cs`. It resolves from the host the
integration fixture starts, which runs `AddOrderingApplication` and
`AddOrderingInfrastructure`, derives its assemblies from the host's references
that begin `Ordering.`, and carries a companion,
`The_scan_reaches_both_layers_that_hold_handlers`, asserting that the derived
set holds `Ordering.Application` and `Ordering.Infrastructure`.

The three guards' worked examples: `IProductPriceReader` unregistered fails
`ValidateOnBuild`, because `PlaceOrderHandler` needs it;
`ProductPriceProjection` unregistered fails only the registration test; and
`ServiceIdentityOptions` unbound fails only `ValidateOnStart`. The queue
`ordering-catalog-events` binds Catalog's product events
([§9.8](09-messaging.md)), so an unregistered `ProductPriceProjection` is also
a message on the error queue.

The EF implementation of `IUnitOfWork` is
`<Service>.Infrastructure/Persistence/EfUnitOfWork.cs` in every service. The
test that a handler which writes raw and then fails leaves no row,
`The_behaviour_leaves_no_row_when_a_handler_writes_raw_and_then_fails`, is in
each service's `DatabaseSmokeTests.cs`, Ordering's in
`tests/Ordering.Api.Tests/`. The transaction behaviour's reason for forbidding
a network call is the dependency Ordering would otherwise take on Catalog: a
slow Catalog would make Ordering look down.

### E.4.3 PlaceOrder and where its prices come from

`PlaceOrderCommand`, its validator and its handler are in
`Ordering.Application/Orders/PlaceOrder/`. Its operation name is
`ordering.order.place`, and the bounds its validator shares with `Web.Bff` are
`OrderLimits`. A product with no available price fails with
`order.products_unavailable`, an `Error.Rule` answered as a 422.

Prices are Catalog's. `IProductPriceReader` is implemented by
`ProjectedPriceReader`, in `Ordering.Infrastructure/Persistence/`, over
`ordering.ProductPrices`, which Catalog's `ProductPublished`, `PriceChanged`
and `ProductDiscontinued` keep current. The gRPC pricing client of
[§9.7](09-messaging.md) is a different caller, the BFF's.
`PublishProductValidator` and its rule for a name are
[ADR-085](adr/ADR-085-a-published-products-name-is-visible-text-and-its-thumbnail-is-https-from-a-host-the-seller-chooses.md)'s, and its rule for an image is
a key's bound, which [E.6.11](#e611-the-product-image-upload) and
[ADR-106](adr/ADR-106-an-s3-compatible-object-store-for-product-images-the-bff-issues-a-pre-signed-upload-url-472.md)
own.

### E.4.4 The queries of Ordering and Catalog

Ordering's one query in `src/` is `GetDeliveryAddressQuery`. The order-history
query of [§6.5](06-cqrs.md), `GetOrderSummariesQuery` with
`Ordering.Application.Orders.GetOrderSummaries`, is specified and not built:
the buyer's order history is the BFF's
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)).
Its level 1 form sums a total from `ordering.OrderLines` because
`ordering.Orders` has no `TotalAmount` column.

Catalog's `GetPrices` is the one enumerating query exempt from pagination; its
ceiling is `GetPricesValidator.MaxProductIds`. Catalog's one-product read,
`GET /v1/catalog/products/{id}`, is `GetProductQuery` and answers with
`ProductSummaryDto`, whose `QuantityAvailable` is `null` for a level Inventory
has never reported. Its listing, `GetProductsQuery`, adds `Q` and `Sort` (one
of `ProductSort`'s values), and `ProductCursor` carries both through
`Cursor.Wrap` ([ADR-073](adr/ADR-073-the-product-listing-takes-a-search-and-a-closed-sort-and-its-cursor-carries-both.md),
[ADR-074](adr/ADR-074-a-seller-reads-their-own-products-and-withdraws-one-and-a-withdrawal-is-final.md)).

### E.4.5 The Ordering escalation

The trigger is the customer order history screen, which needs product names and
images that live in Catalog. The escalation is Ordering's and its lifecycle
half is built: `OrderSummaryProjection` keeps `ordering.OrderSummaries` from
five lifecycle events, `OrderPlacedDomainEvent`,
`OrderStockConfirmedDomainEvent`, `OrderConfirmedDomainEvent`,
`OrderShippedDomainEvent` and `OrderCancelledDomainEvent`, is
`OrderMetrics`' only call site, and gives Ordering's `projection.lag` its first
writer. Not built: `ordering.Products`, its `ProductPublished` handler, and the
history query at either level.

| Table | Fed by | Read by |
|---|---|---|
| `ordering.OrderSummaries` | The five lifecycle events above, on the local lane | The escalated history query, which replaces the level 1 one |
| `ordering.Products` | Catalog's `ProductPublished`, from the broker | The same query, to resolve the ids a summary stores |
| `ordering.ProductPrices` | Catalog's `PriceChanged`, `ProductPublished` and `ProductDiscontinued` | `IProductPriceReader`, on the write path |
| `ordering.ProductWithdrawals` | Catalog's `ProductDiscontinued` | The price upsert, which consults it and nothing else reads |

`OrderSummaryProjection` implements `IProjectionHandler<T>`, for Ordering's own
events, and `ProductPriceProjection` implements `IIntegrationEventHandler<T>`,
for Catalog's. They are
`Ordering.Infrastructure/Projections/OrderSummaryProjection.cs` and
`Ordering.Infrastructure/Projections/ProductPriceProjection.cs`.
`OrderStockConfirmedDomainEvent` is absent from [§9.3](09-messaging.md)'s
publish allow-list as internal bookkeeping no service subscribes to
([§3.2](03-bounded-contexts.md)); `AwaitingPayment` is a state only the
customer's own history shows.

### E.4.6 The price projection's rebuild

Catalog republishes the facts it still holds from its migrator image, with
their original stamps
([ADR-090](adr/ADR-090-catalog-republishes-the-facts-it-still-holds-from-the-migrator-image-with-their-original-stamps.md)),
which is the rebuild procedure for both `ordering.ProductPrices` and
`ordering.Products`, because Ordering has no source of truth for either. The
stamps are `PublishedAt`, `LastEventAt` and `WithdrawnAt`, and Catalog orders a
product's events by its row version
([ADR-075](adr/ADR-075-a-products-events-are-stamped-in-the-order-its-writes-committed.md)).
Everything published before `ordering-catalog-events` was first declared is
absent from Ordering, because the broker drops what no queue is bound for.

## E.5 §9: messaging and the sagas

[§9](09-messaging.md) states the rules; this section holds the reference
domain's cases that its evidence blocks and its one saga argue from. A queue
name, a contract's members and a retry ladder's numbers are the code's
([ADR-096](adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)
part 3), so each subsection names the file that holds them and does not copy
them.

### E.5.1 The contracts

Contracts live under `src/BuildingBlocks/Common.Contracts/<Service>/V1`, one
namespace per owning service, and every event is an `IIntegrationEvent`.

| Owner | Events it publishes | Commands it accepts |
|---|---|---|
| Ordering | `OrderPlaced`, `OrderConfirmed`, `OrderCancelled` | `CancelOrder`, `ConfirmOrder`, `MarkOrderShipped`, `FlagOrderForReview` |
| Inventory | `StockReserved`, `StockReservationFailed`, `StockReleased`, `StockLevelChanged` | `ReserveStock`, `ReleaseStock` |
| Payments | `PaymentAuthorised`, `PaymentDeclined`, `PaymentRefunded` | `AuthorisePayment` |
| Shipping | `ShipmentDispatched`, `ShipmentDelivered` | |
| Catalog | `ProductPublished`, `PriceChanged`, `ProductDiscontinued` | |
| Privacy | `PersonalDataDeleteRequested` | `PersonalDataDeleteCompleted` |

[E.1.1](#e11-32-the-service-responsibilities-table)'s Accepts and Consumes columns are the owner of
who binds what. `OrderPlaced`, `OrderConfirmed` and `OrderCancelled` each carry
`ErasedUnder`, optional on all three
([ADR-099](adr/ADR-099-an-order-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-placed.md)).
`PlacedLine` and `ConfirmedLine` are each their own record, and `StockLine` is
the stock command's, so that versioning an order event does not version a
command. `AuthorisePayment` carries an amount and a currency and no subject
([ADR-028](adr/ADR-028-a-money-movement-command-carries-no-subject.md));
Payments holds the order, payer included, and derives the subject from it.

The allow-list that turns Ordering's domain events into contracts is
`OrderingIntegrationEventMapper` in `Ordering.Application/Integration`; its
command mappers, one per accepted command, are
`Ordering.Infrastructure/Messaging/CommandMappers.cs`.
`OrderStockConfirmedDomainEvent` is kept off the allow-list on purpose, and the
mapper argues why.

A persisted `MessageType` is written from the domain event's name, for example
`Ordering.Domain.Orders.Events.OrderPlacedDomainEvent`. The rename sample in
§9.4 is that event's, from `OrderPlaced` to `OrderPlacedDomainEvent`, with
`Ordering.Domain.Orders.Events.OrderPlaced` as the old name the alias resolves.
Two handlers of one event under one inbox row are, in §9.5, Ordering's price
projection and its handler for `ProductPublished` ([§6.6](06-cqrs.md)).

### E.5.2 The queues and broker identities

Each queue name is held once, as a constant, in the owning service's
`DependencyInjection.cs` under `*.Infrastructure/Messaging`; the saga's
addresses for them are `Endpoints` in `Ordering.Infrastructure/Messaging`.

| Service | Command queue | Event queues it declares |
|---|---|---|
| Ordering | `ordering-commands` | `ordering-catalog-events`, `ordering-stock-events`, `ordering-fulfilment-saga`, `ordering-privacy` |
| Inventory | `inventory-commands` | `inventory-events` |
| Payments | `payments-commands` | `payments-events`, `payments-privacy` |
| Shipping | | `shipping-events`, `shipping-privacy` |
| Notifications | | `notifications-events`, `notifications-privacy` |
| Catalog | | `catalog-inventory-events` |
| Privacy | `privacy-completions` | |

The three queues the saga addresses are `inventory-commands`,
`payments-commands` and `ordering-commands`: `ReserveStock` and `ReleaseStock`
go to the first, `AuthorisePayment` to the second, and the four Ordering
commands, which the saga sends to its own service, to the third. The
projection endpoint carries Catalog's three events into Ordering's price
projection; the stock-events endpoint carries `StockReserved` to the consumer
that records it on the aggregate, and the saga's queue carries it to the
machine.

Each service authenticates to the broker as `<service>-svc`, with `bff-svc`
and `dead-letter-operator` beside them, in
`deploy/compose/rabbitmq/definitions.json`
([ADR-036](adr/ADR-036-the-broker-has-a-per-service-identity.md),
[ADR-072](adr/ADR-072-a-person-replays-dead-letters-as-a-broker-account-of-its-own.md)).
`catalog-svc` therefore has no `write` matching `ordering-commands` or the
Ordering contracts' exchanges, and `deploy/compose/rabbitmq/check_permissions.py`
asserts that no service's `write` covers another's command endpoint.

### E.5.3 The fulfilment saga

`OrderFulfilmentSaga` and `OrderFulfilmentState` are in
`Ordering.Infrastructure/Messaging`, with the five scheduled expiry messages in
`FulfilmentTimeouts.cs`. The machine starts on `OrderPlaced` and sends
`ReserveStock`, then `AuthorisePayment`, then `ConfirmOrder` to Ordering's own
queue, and ends on `ShipmentDispatched` with `MarkOrderShipped`. Its waits are
on Inventory (`StockTimeout`), Payments (`PaymentTimeout`), Ordering itself
(`ConfirmationTimeout`), Shipping's despatch (`DespatchTimeout`) and a
`ReleaseStock` it sent (`ReleaseTimeout`).

Two services owe `Compensating` an answer, Inventory for the reservation and
Payments for the verdict, which is why it joins on both
([ADR-025](adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)).
Inventory consumes `OrderCancelled` directly and answers every release,
including a no-op one
([ADR-024](adr/ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md),
[ADR-029](adr/ADR-029-inventory-releases-on-the-cancellation-not-on-the-sagas-word.md)).
Payments consumes `OrderCancelled` too and voids an authorisation already
taken; no service accepts a refund command, so the saga escalates through
`FlagOrderForReview` instead. `PaymentAuthorised` is produced by Payments, so
a missing instance for it always faults. The saga's endpoint takes
MassTransit's own outbox on `OrderingDbContext`
([ADR-032](adr/ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)),
and `FlagOrderForReviewHandler` writes `ordering.OrderReviews` from
`Ordering.Application/Orders/FlagOrderForReview`.

An order's consumer for `StockReserved` records it on the aggregate by
dispatching a command, and `Order.ConfirmStock` is the transition it asks for.
`OrderStockConfirmedDomainEvent` is local-only, so its one row is the Local
one `OrderSummaries` earns ([§6.6](06-cqrs.md)).

### E.5.4 The synchronous hops

| Hop | Caller | Callee | Registered and sized in |
|---|---|---|---|
| Pricing | `Web.Bff` | Catalog, `pricing.proto` | `src/BFF/Web.Bff/PricingHop.cs` |
| Device registration and preferences | `Web.Bff` | Notifications.Api, `devices.proto` and `preferences.proto` | `src/BFF/Web.Bff/NotificationsHop.cs` |
| Delivery address | Shipping's worker | Ordering | `Shipping.Infrastructure/Addresses/AddressHop.cs` |
| Contact | Notifications' worker | Keycloak's admin API | `Notifications.Infrastructure/Contacts/ContactHop.cs` |
| Payment provider | Payments' consumers | the provider | `Payments.Infrastructure/Provider/ProviderHop.cs` |
| Carrier | Shipping's worker | the carrier | `Shipping.Infrastructure/Carrier/CarrierHop.cs` |
| Mail relay | Notifications' worker | the relay | `Notifications.Infrastructure/Mail/MailHop.cs` |
| FCM | Notifications' worker | FCM, and Google's token endpoint | `Notifications.Infrastructure/Push/FcmHop.cs` |
| APNs | Notifications' worker | APNs | `Notifications.Infrastructure/Push/ApnsHop.cs` |

Catalog owns `pricing.proto` in `Catalog.Api/Protos`, and `Web.Bff` links it;
Notifications owns `devices.proto` and `preferences.proto` in
`Notifications.Api/Protos`, and `Web.Bff` links both too, one gRPC client each
on the one hop's numbers. The command path never calls Catalog: it reads
Ordering's price projection. The deepest chains on a request path are
`Client → Gateway → BFF → Catalog` and `Client → Gateway → BFF →
Notifications.Api`, and neither callee calls anybody. The registration and the
preferences carry the caller's own token and no client credentials
([ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md),
[ADR-120](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)). The two worker reads of the address and the contact are
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s,
the contact only for a verified email
([ADR-086](adr/ADR-086-an-unverified-email-is-no-contact.md)), and they spend
no hop of the budget. Shipping cannot function without a delivery address, and
`OrderConfirmed` carries none
([ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)),
which is why the worker reads it from Ordering. The one consumer call granted
is Payments' provider call
([ADR-070](adr/ADR-070-payments-calls-its-provider-inside-its-consumers.md)),
and `ProviderKillSwitch` in `Payments.Infrastructure/Messaging` stops its
receive endpoints when the provider is unavailable. Catalog's `Money` refuses a
negative amount, which the BFF's quote does not rely on. The consumer-driven
contract's two suites are `tests/Web.Bff.Tests` (`UpstreamRetryTests`,
`ResilienceHierarchyTests`) and Catalog's own.

The two push hops are registered for each platform an app names, and
`PushSendWorker` calls them, one delivery a call; an unregistered answer
deletes the device row in the transaction that ends the delivery
([ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md),
[ADR-121](adr/ADR-121-a-sign-out-above-a-cleared-device-row-moves-its-sequence-whoever-sends-it-and-an-invalid-token-answer-keeps-the-row-1034-474.md)).
Each is a third party behind its own breaker, so one provider's
outage parks its own platform and neither mail nor the other provider.

### E.5.5 The retry ladders

Every service has a `RetryPolicy` in `*.Infrastructure/Messaging`; the values
are each file's. In Ordering the ladder applies to the receive endpoints that §9.8 prints,
`ordering-commands` adding the `ContractMappingException` exclusion and
`ordering-fulfilment-saga` swapping the in-memory outbox for MassTransit's. Inventory's applies to every endpoint its own
`DependencyInjection` declares, and Catalog's to `catalog-inventory-events`.
§9.6's confirmation wait has to clear Ordering's ladder and no other.

### E.5.6 The workers that claim their own rows

Shipping's `FulfilmentWorker` starts its pass as a child of the trace that
stamped the shipment, when it is recorded and again when its cancellation is
requested, so the booking or the carrier's cancellation sits in the confirming
or cancelling trace; its `TrackingWorker` links instead of parenting, because
it repeats for the shipment's whole life, and the despatch a poll stages
travels in the poll's trace, one link from the order's. Notifications'
`SendWorker` runs a notice's send as a child of the trace that stamped it at
intake. All three read `StagedTrace` and map `StagedTraceColumns` in
`Common.Infrastructure/Tracing`.

## E.6 §10 and §11: the gateway routes and the identity realms

### E.6.1 The gateway routes

The route file is the `ReverseProxy` section of
`src/Gateway/Gateway.Api/appsettings.json`, and it is the owner of the list;
this section says only what the chapter's rules were drawn from. Its eight
routes, by cluster and policy:

| Route | Cluster | Policy | Matches |
|---|---|---|---|
| `catalog-public` | `catalog` | `anonymous` | `GET /api/v1/catalog/{**catch-all}` |
| `catalog-write` | `catalog` | `authenticated` | `POST` and `PUT` on the same path |
| `catalog-own` | `catalog` | `authenticated` | `GET /api/v1/catalog/products/mine` |
| `ordering` | `ordering` | `authenticated` | `/api/v1/orders/{**catch-all}` |
| `inventory-admin` | `inventory` | `inventory:admin` | `/api/v1/inventory/{**catch-all}` |
| `payments-admin` | `payments` | `payments:admin` | `/api/v1/payments/{**catch-all}` |
| `privacy-erase` | `privacy` | `privacy:erase` | `/api/v1/privacy/{**catch-all}` |
| `web-bff` | `web-bff` | `authenticated` | `/bff/v1/{**catch-all}` |

**Catalog's one-product read is served by the public catch-all**, under
`anonymous`, so a product's deep link needs no route of its own. The one Catalog
`GET` it must not serve is a seller's own list, which names a caller:
`catalog-own` matches that literal path and holds it to `authenticated`
([ADR-074](adr/ADR-074-a-seller-reads-their-own-products-and-withdraws-one-and-a-withdrawal-is-final.md)).
**Catalog's write side is `catalog-write`**: the endpoint still requires
`catalog:write`, and the route's `authenticated` is the edge's coarser check.

**The BFF makes two synchronous calls, to Catalog and to Notifications.Api**
([§2.2](02-architecture-at-a-glance.md), [§9.7](09-messaging.md),
[ADR-017](adr/ADR-017-one-synchronous-hop.md)), each its own request's one
hop, so those two are the hops `CorrelationIdHandler` is registered on.

### E.6.2 Error catalogue and refusals

**Ordering's error catalogue** is
`src/Services/Ordering/Ordering.Application/Orders/OrderErrors.cs`, one static
class in which every `Error` the service returns is built. Excerpted:

```csharp
namespace Ordering.Application.Orders;

/// <summary>Every <see cref="Error"/> the service returns is built here, so <c>Code</c> is a closed set.</summary>
/// <remarks><c>Code</c> is a metric dimension, so no id or count goes in one (§10.5).</remarks>
public static class OrderErrors
{
    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    /// <summary>For <c>Delivered</c> as well as <c>Shipped</c>; one code, being a §9.8 dimension.</summary>
    public static readonly Error AlreadyShipped =
        Error.Rule(
            "order.already_shipped",
            "An order that has already shipped cannot be cancelled; raise a return instead.");

    // 422, not 400: the request was well-formed and the validator passed it.
    // The products are unpriceable, which is a fact about this service's state
    // and not something the caller phrased wrongly.
    public static Error ProductsUnavailable(IReadOnlyList<ProductId> missing) =>
        Error.Rule("order.products_unavailable", $"No price for {missing.Count} product(s).");
}
```

`ProductsUnavailable` is the shape to copy: one code, a description that
varies. `AlreadyShipped` answers a delivered order as well as a shipped one, so
its description is worded to be true of both.

**Privacy's lock refusal** is `ErasureLockNotTakenException`, thrown by
`ErasureRequestRepository.TakeLockAsync`, answered 503 with `Retry-After` and
`code` `erasure_request.busy` by `ErasureLockNotTakenExceptionHandler`. It is
the only service that takes such a lock.

### E.6.3 Notice mail correlation

Notifications' send worker puts the originating event's `CorrelationId` on
every message it hands the relay, as `SmtpMailChannel.CorrelationIdHeader` in
`Notifications.Infrastructure`, `X-Correlation-Id`. A test holds it equal to
`CorrelationIdExtensions.Header`, because the project that speaks SMTP does not
reference `Common.Web`.

### E.6.4 The buyer order read

The chapters name services by role. In the reference domain **the order
service is Ordering, the product service is Catalog, the payment service is
Payments, the stock service is Inventory, the shipping service is Shipping,
the notification service is Notifications and the privacy service is
Privacy**.

Shipping's spec, written against [E.6.10](#e610-the-buyers-order-read)'s
contract, is `docs/superpowers/specs/2026-09-22-shipping-service-design.md`; the
events it publishes are those of [E.5.1](#e51-the-contracts), and E.6.10 states
what that settles for the buyer's read.

### E.6.5 Permissions and policies

Each service's permission constants are one class at its composition root; the
code is the owner of the strings, and this section the owner of the reasons.

| Service | Constants | Why this set |
|---|---|---|
| Ordering | `orders:write`, `orders:cancel`, `orders:delivery-address`, `orders:saga-read` | There is no `orders:read`, because nothing in Ordering requires one for an order's own data; `orders:saga-read` is staff's, since the saga instance holds no customer (ADR-028) to check a caller against |
| Catalog | `catalog:write` | Its listing is anonymous (§10.2), so there is no `catalog:read` |
| Inventory | `inventory:admin` | Registered at the gateway as well (§10.2) |
| Payments | `payments:admin` | Registered at the gateway as well (§10.2) |
| Privacy | `privacy:erase` | Registered at the gateway as well (§10.2) |

The gateway's own three are `GatewayPermissions` in `Gateway.Api`, and its
`Program.cs` registers one policy for each.

Ordering's registration, in `src/Services/Ordering/Ordering.Api/Program.cs`:

```csharp
// RequirePermission, so the claim type is PermissionClaim.Type's alone (§11.4). No orders:admin policy: that string
// is a claim CancelOrderHandler checks against a loaded aggregate.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress))
    .AddPolicy(OrderingPermissions.SagaRead, p => p.RequirePermission(OrderingPermissions.SagaRead));
```

Ordering's constants are
`src/Services/Ordering/Ordering.Api/OrderingPermissions.cs`:

```csharp
public static class OrderingPermissions
{
    public const string Write = "orders:write";
    public const string Cancel = "orders:cancel";
    public const string DeliveryAddress = "orders:delivery-address";
    public const string SagaRead = "orders:saga-read";
}
```

`orders:admin` is in neither: it is a claim `CancelOrderHandler` checks against
a loaded aggregate. `GrantablePermissionTests` sits in `Gateway.Api.Tests`,
`Catalog.Api.Tests`, `Ordering.Api.Tests`, `Inventory.Api.Tests`,
`Payments.Api.Tests` and `Privacy.Api.Tests`.

### E.6.6 The cancel walk-through

§11.4 quotes four samples, all Ordering's:

| Sample | File |
|---|---|
| The cancel endpoint, and `CancelOrderRequest` at the foot of the same file | `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs` |
| `CancelOrderCommand` and `CommandOrigin` | `src/Services/Ordering/Ordering.Application/Orders/CancelOrder/CancelOrderCommand.cs` |
| `CancellationReasons`, and `CancellationOrigins` beside it | `src/Services/Ordering/Ordering.Application/Orders/CancellationReasons.cs` |
| The body of `CancelOrderHandler.HandleAsync` | `src/Services/Ordering/Ordering.Application/Orders/CancelOrder/CancelOrderHandler.cs` |

A sibling service sending a cancel code Ordering does not know is a deployment
problem: the message mapper throws `ContractMappingException`, which §9.4's
retry policy ignores.

### E.6.7 Payer resolution

[ADR-028](adr/ADR-028-a-money-movement-command-carries-no-subject.md) in the
reference domain:

- **Payments resolves the payer from the `OrderPlaced` it consumes**
  ([§3.2](03-bounded-contexts.md)), so §9.6's `AuthorisePayment` carries no
  `CustomerId`. Its record holds the payer as well as the total.
- **Publishing the command is scoped**: `payments-commands` is writable by the
  services whose own source addresses it
  ([ADR-036](adr/ADR-036-the-broker-has-a-per-service-identity.md)).
- **Forging the seeding `OrderPlaced`** needs `write` on
  `Common.Contracts.Ordering.V1:OrderPlaced`, which only Ordering's account
  holds. The event is one Ordering's own saga and Notifications both consume.

### E.6.8 The worker clients

Two workers hold a client of their own
([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)):
Shipping's worker, whose `shipping-worker` client holds one role on
`commerce-api` because the address owner serves its read as a method, and
Notifications' worker, whose `notifications-worker` client holds `view-users` on
`realm-management` because the mailbox's owner is the realm itself. Their rows
in §11.5's table are the realm's settings.

The suite proving the BFF's and `shipping-worker`'s grants is
`Web.Bff.Tests`; `Shipping.Worker.Tests` does not start a Keycloak, and
`notifications-worker`'s grant is proved in `Notifications.Worker.Tests`
against a Keycloak of its own. The minimal host those tests call runs
`AddJwtAuthentication` and is not Catalog.

### E.6.9 Personal data erasure

The holders in the reference domain are Ordering, Notifications, Shipping,
Payments and the BFF, and Privacy owns the request; the diagram in §11.7 is
this case, and this table is the owner of what each edge of it erases:

| Holder | Erases | Reports as |
|---|---|---|
| Ordering | `Orders.CustomerId` anonymised, address purged, `OrderSummaries.CustomerId` cleared | `ordering` |
| Notifications | ended `NotificationLog` and `PushDeliveries` rows anonymised, contacts, waiting notices, pending deliveries and `OrderRecords` deleted, device rows cleared to their installation id and sequence ([ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)), keeping the last-seen time the retention pass deletes them by ([ADR-118](adr/ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)), consent journal entries anonymised, keeping their topic, channel, opt-in, times, client and chain as the evidence that the sends before the erasure were lawful ([ADR-120](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)) | `notifications` |
| Shipping | `DeliveryAddresses` rows deleted (ADR-052), `Shipments.ErasedUnder` emptied (ADR-103) | `shipping` |
| Payments | `PaymentOrders.CustomerId` anonymised | `payments` |
| The BFF | order ids recorded, the subject's `bff.Orders` rows deleted (ADR-098), and their saved address unless it was saved under the request (ADR-107, ADR-135) | `bff` |

`PersonalDataDeleteRequested` and `PersonalDataDeleteCompleted` are both in
`Common.Contracts.Privacy.V1`. Privacy publishes the request and accepts the
completion as a command **sent** to its queue by each holder
([ADR-094](adr/ADR-094-a-holder-reports-its-erasure-by-sending-to-privacys-queue-and-does-not-publish-it.md)).
The holders that write from an event feed and so ask their own audit rows after
they write are the BFF's projection, Notifications' intake, Payments' intake and
Shipping's fulfilment worker
([ADR-097](adr/ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)).
Privacy is a seventh service and its responder set is fixed when a request is
raised
([ADR-092](adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md));
`OrderConfirmed` carries no postal address although Shipping cannot act without
one, which Shipping reads from its owner instead (ADR-052).

### E.6.10 The buyer's order read

[ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)
decides where this read is served from: a projection `Web.Bff` owns, and no
synchronous call. What follows is its wire contract, which the one consumer
helped write and builds against.

Two routes, because a list and a detail answer different questions and §10.1's
rule is that the screen's question is the whole of the request (a third, the
detail's event stream, follows the fields):

| | Path |
|---|---|
| Client calls | `/bff/v1/orders?cursor=&limit=` |
| Gateway matches | `/bff/v1/{**catch-all}` |
| Gateway strips | `/bff` |
| Service receives | `/v1/orders` |
| Service maps | `MapGroup("/v1/orders")` |

The detail route is that group's `/{id:guid}`. Neither needs a gateway change,
the subject is the principal's, and an order another buyer owns answers 404 and
not 403, as cancel already does, all by
[§10.7](10-api-gateway.md#107-a-projection-read-and-its-event-stream). The
list's `limit` is clamped server-side to `OrderPage.MaxLimit`.

#### The status a buyer is shown

A closed vocabulary, for [§10.7](10-api-gateway.md#107-a-projection-read-and-its-event-stream)'s
reason.

| Status | Reached by |
|---|---|
| `placed` | `OrderPlaced` |
| `confirmed` | `OrderConfirmed` |
| `dispatched` | `ShipmentDispatched` |
| `cancelled` | `OrderCancelled`, below |
| `out_of_stock` | `OrderCancelled`, below |
| `declined` | `OrderCancelled`, below |
| `delivered` | `ShipmentDelivered` |

**The status is ranked, as §10.7 requires.** Rank rises as the table reads,
`placed` lowest and `delivered` highest, with the three `OrderCancelled` members
sharing one rank because one event decides between them; `BuyerStatus.Of` is
the order. `ShipmentDispatched` arriving before `OrderConfirmed` is ordinary
rather than exotic. §6.6 buys the same property for `ordering.OrderSummaries`
with `UpdatedAt < @OccurredAt`, which §10.7 says cannot be copied: four hosts
mint these timestamps.

A cancellation therefore outranks a despatch, and the case is real rather
than defensive. `Order.Cancel` refuses once the order is `Shipped`, so an
`OrderCancelled` on the wire is proof the order was cancelled before
despatch; §9.6's compensation can still leave a `ShipmentDispatched` behind
it, which is the interleaving `MarkOrderShippedHandler` answers without
shipping anything. Showing the later event would tell a buyer their cancelled
order is on its way.

##### Which cancellation a buyer is told about

**Keyed on `Origin` first and `Reason` second**, because `Reason` alone is
wrong. `Order.Cancel` records the origin and never checks it, and although the
cancel endpoint takes `customer_request` alone
([ADR-087](adr/ADR-087-a-caller-cancels-an-order-only-as-a-customer-request.md)),
an `OrderCancelled` recorded before that rule can pair a user origin with a
workflow code, and an error queue or a replay can still deliver one — and a
`Reason`-only map answers a buyer who cancelled with `payment_declined` by
telling them their card was refused.

| `Origin` | `Reason` | Status |
|---|---|---|
| `user` | any | `cancelled` |
| `workflow` | `out_of_stock`, `stock_timeout` | `out_of_stock` |
| `workflow` | `payment_declined`, `payment_timeout` | `declined` |
| `workflow` | `customer_request` | `cancelled` |
| absent | any | `cancelled` |

`Origin` is optional and additive under §9.2: absent means the payload
predates the field, which an error queue or a replay can deliver at any time.
It reads as `cancelled` because that is the member that claims least — an
absent origin is no evidence that the platform refused anything, and
inventing a refusal is the harmful direction of a wrong guess.

The two collapses are the platform's distinctions rather than the buyer's:
stock that was never there and stock that did not answer in time are both
`out_of_stock`, and an authorisation declined and one that timed out are both
`declined`. A payment provider's own decline reasons never travel — they are
its codes and an open set (ADR-049) — so they stop at the service that owns
them.

**Neither the saga's states nor `OrderStatus` reaches the client**, and the
two do not agree with one another in any case (§9.6, §5.4). `AwaitingStock`
and `AwaitingPayment` are both `placed` to a buyer, which is also forced:
`OrderStockConfirmedDomainEvent` is not on the order service's publish allow-list, so
no consumer can tell them apart. `Compensating` has no buyer meaning at all —
what the buyer is owed is the outcome it works towards, and that arrives as
`OrderCancelled`.

> **`dispatched` and `delivered` are the shipping service's.** In the
> reference domain Shipping publishes both
> ([Appendix E.1.1](appendix-e-reference-domain.md#e11-32-the-service-responsibilities-table))
> and its spec is written against this contract
> ([Appendix E.6.4](appendix-e-reference-domain.md#e64-the-buyer-order-read)).

#### The fields both routes carry

- **`asOf`**, the BFF's own clock at the last write to this order's row. One
  value from one clock, which is what makes it well defined when four streams
  feed the row; §10.7 states what it does and does not say, and the BFF has no
  write model to read a consistent status from the way §6.6's own remedy
  assumes.
- **`refunded`**, a flag and a timestamp beside the status rather than a
  member of it. `Refund.Voided` has one caller, the handler for
  `OrderCancelled`, and it runs only where an authorisation exists — so every
  refund follows a cancellation, and a refund as a status would overwrite the
  `out_of_stock` and `declined` distinction the table above exists to draw.
- **`cancellable`**, a hint as §10.7 words it. Only the order service knows:
  the rule is `Order.Cancel`'s, and during the window between a despatch
  reaching the saga and reaching this queue the hint reads `true` while the
  command would answer 422, so the client keeps its `order.already_shipped`
  handling.
- **A timestamp per status reached**, so a timeline is drawn from facts rather
  than from arithmetic on the client. Every event named above carries
  `OccurredAt`. They come from four hosts, so they order a timeline a person
  reads and never a decision the code makes — the rank above is what decides.
- **Money as the server's numbers**, an amount and a currency per line and in
  total, as §10.7 requires. **An order no placed or confirmed event has reached
  has no lines and a null total**: `OrderCancelled` supplies the customer and
  nothing priced, and the row it created is shown as far as the projection knows
  it rather than hidden until it knows more.

The detail route adds the quantity and unit price each line was placed with,
and the payment outcome where there is one to give — an authorisation or a
refund. A declined payment has none: `PaymentDeclined` carries only a reason
that may not travel, so the order's own status is what says so.

**`productName` is nullable, and the null is not an edge case.** The name is
resolved on read from the product service's stream, not snapshotted with the
line, so a product published before this projection's queue was declared has
no name to resolve — the broker drops what no queue is bound for. §6.6
documents the same gap for `ordering.Products`, including its rebuild, which
in the reference domain is the product service's republish
([Appendix E.4.6](appendix-e-reference-domain.md#e46-the-price-projections-rebuild)),
and that `PriceChanged` is no back door because it carries no name. Here the
consequence is softer than the order service's 422 and quieter for it:
a blank line in order history with no error anywhere, which is why the
contract names it rather than leaving a client to meet it.

#### An order the projection cannot yet attribute

Both shipment events and the payment events ADR-051 lists carry an order id
and no customer, the payments by ADR-028's decision that a money movement
carries no subject. They are attributed by joining to a row one of the order
service's three events created, and §9.4 does not promise that one did.

**§10.7's rule applies to both routes**: the projection inserts on a missing
row rather than dropping the event, and a row with no owner is never returned.
It stays invisible until an order-service event supplies the customer, and if
none ever arrives it stays invisible; §6.6 names the dropped event as a defect
that leaves an order reading one status for ever with no error anywhere.
ADR-051's rebuild replays the publishers' outboxes, so an order whose
`OrderPlaced` has aged out and whose `ShipmentDelivered` has not is exactly such
a row. RabbitMQ keeps
nothing a consumer has acknowledged, so the last copy of a delivered event is
its publisher's processed outbox row, kept for `RetentionPolicy.OutboxWindow`
(§9.4), and `tools/bff-replay` replays those rows to this projection's queue
alone.

> **What Shipping settled, and what it was not asked for.** In the reference
> domain ([Appendix E.6.4](appendix-e-reference-domain.md#e64-the-buyer-order-read))
> the shipment half of the detail route is as rich as Shipping's published
> events, and
> [E.5.1](appendix-e-reference-domain.md#e51-the-contracts) names two, and
> the payloads in `Common.Contracts.Shipping.V1` carry an order id and a
> tracking number between them.
> So a buyer sees despatch, delivery and a number to take to the carrier, and
> **no tracking feed**: Shipping publishes no tracking event, and the two
> milestones are the timeline (its spec, which E.6.4 names).
> What its spec was **not** asked for is a read endpoint — ADR-051 exists so
> that it is not, and E.1.1, §4.1, §13 and §15 all describe a Shipping that
> has none.

#### The order's event stream

[ADR-108](adr/ADR-108-the-buyers-order-has-a-server-sent-event-stream-that-polls-its-row-and-ends-inside-the-deadline-475.md)
adds a third route to the group, for a client that would otherwise poll the
detail route: `GET /bff/v1/orders/{id}/events`. It answers `text/event-stream`,
with `Cache-Control: no-store` and `X-Accel-Buffering: no`, and
[§10.7](10-api-gateway.md#107-a-projection-read-and-its-event-stream) states
the rules it keeps. The names those rules use here:

- **`order` events.** `data` is the detail route's `OrderDetail`, byte for byte
  the shape it returns, and `id` is that row's `asOf`.
- **Heartbeat.** A comment line, `: keep-alive`, after
  `OrderEventStream.HeartbeatInterval` with nothing sent.
- **Reconnection.** The stream names `OrderEventStream.RetryAfter` in its
  `retry` field.
- **The stream ends by itself.** After `OrderEventStream.Lifetime`, which is
  inside `ServiceOptions.OperationTimeout`; at once on a delivered order, the
  one status nothing follows; and when the row is gone, as an erasure removes
  it.

Each open stream reads its one row by key every `OrderEventStream.PollInterval`,
so an update reaches the client one interval after the projection writes it.

### E.6.11 The product image upload

[ADR-106](adr/ADR-106-an-s3-compatible-object-store-for-product-images-the-bff-issues-a-pre-signed-upload-url-472.md)
decides that the bytes of an image go from the client to an S3-compatible store
and through no service. What follows is the wire contract the consumer builds
against; [§10.8](10-api-gateway.md#108-a-client-supplied-file) states the rules
that make a client-supplied file safe to publish.

| | Path |
|---|---|
| Client calls | `POST /bff/v1/product-images` |
| Gateway matches | `/bff/v1/{**catch-all}` |
| Service receives | `/v1/product-images` |

It needs no gateway change, as §10.7 states. The caller is an authenticated
seller, holding the permission that publishing a product already requires
([§11.4](11-identity-authorization.md)); the seller is bound from the
principal, and the request names none.

**The request carries two values and the server chooses everything else.**
`contentType` is one of `image/jpeg`, `image/png` and `image/webp`, a closed
list in which SVG is deliberately absent: an SVG is a document that can carry
script. `sizeBytes` is at most 5 MiB. A value outside either answers 400 in
§10.5's `Error` vocabulary.

**The response is the URL and what the client must send with it:**

| Field | Meaning |
|---|---|
| `uploadUrl` | A pre-signed `PUT` for one key, expiring after five minutes |
| `headers` | The `Content-Type` and `Content-Length` the signature covers, which the store refuses a request without |
| `key` | The key the server chose, `uploads/{sellerId}/{guid}.{ext}` with the extension taken from the allowed type |
| `expiresAt` | When `uploadUrl` stops working |

**Publishing attaches the image; the request does not send a URL.**
`PublishProduct` carries the `key` the upload returned. The service that owns
products checks the key against the caller's own `uploads/{sellerId}/` prefix
and copies the object to `products/{sellerId}/{guid}.{ext}` with
`Cache-Control: public, max-age=31536000, immutable`, as §10.8 requires, and
stores the public URL it builds from the configured base and the destination
key. A URL from the client is not accepted: the request's `ThumbnailUrl` is
replaced by the `key`, and a product published without one has no image, as a
null `ThumbnailUrl` already means. [ADR-085](adr/ADR-085-a-published-products-name-is-visible-text-and-its-thumbnail-is-https-from-a-host-the-seller-chooses.md)'s `https`
rule becomes a rule about the configured base, which is an absolute `https` URL
(`http` for a loopback host alone, which Compose's local edge is) short enough
that the base and the longest destination key (87 characters) fit
`ThumbnailUrl`'s 400, both checked when the host starts.

**A key that cannot be attached is a 422, and each refusal has its own code.**
`product.image_key_invalid` is a key that is not the shape the BFF mints, is
under another seller's prefix, or arrived with no principal;
`product.image_not_uploaded` is a key with no object behind it, because the
upload was never made or has expired; `product.image_not_an_image` is an object
whose first bytes, or whose stored type, disagree with the type its key
carries. None of them stores a product. A key longer than the longest
destination key is a 400 before any of that, from the validator.

**The expiry rule is on `uploads/`.** Its lifecycle rule deletes objects after
one day, and `products/` has none. A copy whose publish transaction then fails
leaves one object nothing names, which the retry reuses
([ADR-106](adr/ADR-106-an-s3-compatible-object-store-for-product-images-the-bff-issues-a-pre-signed-upload-url-472.md)).

**The two credential pairs are the BFF's, which writes under `uploads/`, and the
product service's, which reads `uploads/` and writes `products/`.** Compose's
local store, its credentials and the chart's store values follow
[§14.1](14-local-development.md), [§15.4](15-cicd-deployment.md) and
`docs/secrets.md`'s exception table, and this section owns only the permissions
above. The local store holds each pair to the policy
[§10.8](10-api-gateway.md#108-a-client-supplied-file) states, so a delete is
refused there as a list is, and `ProductImageStoreTests` proves both.

**The image is served from `products/` alone**, and
`ProductImageStoreTests` reads `X-Content-Type-Options: nosniff` off the local
edge, the one Compose puts in front of the store.

### E.6.12 The saved delivery address

[ADR-107](adr/ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md)
decides that `Web.Bff` keeps one delivery address per customer, as a prefill
for the checkout. What follows is the wire contract, which the one consumer
builds against.

| | Path |
|---|---|
| Client calls | `GET`, `PUT` and `DELETE /bff/v1/saved-address` |
| Gateway matches | `/bff/v1/{**catch-all}` |
| Service receives | `/v1/saved-address` |

It needs no gateway change, and the path is singular, the subject is the
principal's and a repeated `PUT` or `DELETE` leaves the state the first left,
all by [§10.9](10-api-gateway.md#109-a-per-subject-resource).

**The body is the order request's own address**, so a client prefills the
checkout by copying it across: `line1`, `line2`, `city`, `postalCode` and
`country`. `PUT` answers 200 with the address as stored; `GET` answers 200 with
it, or 404 with the code `address.not_found`; `DELETE` answers 204.

**The address is validated by the rule an order applies and by no more.** A
`line1`, a `city` and a `postalCode` are present, a `country` is two ASCII
letters (ISO 3166-1 alpha-2, in either case, stored in upper case), and each
part fits the width the order's own validator admits, so an address that saves
is one an order accepts. A postcode's format is never checked, because no
format is every country's. A `line2` that is blank is stored as none, and every
part is trimmed, as an order stores an address.

**It is a prefill and an input to nothing**: placing an order still carries the
address in the order request, and no service reads the saved one.

**Erasure is the BFF's handler for `PersonalDataDeleteRequested`**, which
deletes the subject's row in the transaction that deletes their orders and
counts it in the request's audit row, unless the row was saved under that
request
([ADR-135](adr/ADR-135-a-saved-address-carries-the-erasure-requests-its-customer-was-already-under-when-it-was-saved-1172-amending-adr-107.md));
the responder set does not change, since
the BFF was already one of the holders an erasure request waits on
([ADR-092](adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)).

### E.6.13 The device registration

[ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
decides that an app registers its installation's push token through
`Web.Bff`, and that the service that sends to the token owns the row — in the
reference domain Notifications, whose API host the BFF calls
([E.5.4](appendix-e-reference-domain.md#e54-the-synchronous-hops)). What
follows is the wire contract, which the app builds against.

| | Path |
|---|---|
| Client calls | `PUT` and `DELETE /bff/v1/devices/{installationId}` |
| Gateway matches | `/bff/v1/{**catch-all}` |
| Service receives | `/v1/devices/{installationId}` |

It needs no gateway change. The installation id is the UUID the app minted
once per install; a path segment that is not one matches no route and answers
404. The subject is the caller's, as [§10.10](10-api-gateway.md#1010-a-write-forwarded-to-the-owning-service)
states.

**A `PUT` registers**, on every start of the app and on every rotation, with
a body of `platform` (`fcm` or `apns`), `appId`, `token` and `sequence`. **A
`DELETE` signs out**, with the sequence as its one query parameter,
`?sequence=`, and no body. The sequence is the installation's counter, raised
by the app before every request. Either answers 204 when the owning service
applies it or when it repeats the write before it, and a sign-out answers
204 whether or not there was a row to clear, and whatever its sequence when
the row is another subject's
([ADR-121](adr/ADR-121-a-sign-out-above-a-cleared-device-row-moves-its-sequence-whoever-sends-it-and-an-invalid-token-answer-keeps-the-row-1034-474.md)).
One out of order answers 409 with the `code` `device.stale_sequence` and a
`sequence` member holding the row's. Both routes are `Convergent`.

**The BFF refuses a malformed request before the hop**, with 400: a platform
outside the pair,
an app id that `PushOptions.Apps` does not name for that platform, a token
that is empty, longer than `DeviceRules.MaxTokenLength` or outside printable
ASCII, and a sequence of zero or below. A sequence that does not bind as an
integer, or a `DELETE` with none, is §10.5's unreadable-request 400 instead.
`PushOptions` is bound from the `Push` section, and an empty list is a
deployment that sends no push and refuses every registration.

**The hop is `NotificationsHop`, with `CallerTokenHandler`** copying the inbound
`Authorization` header, as §10.10 states. A reply `devices.proto` forbids — an
outcome left unset, an applied or repeated write carrying a sequence other than
the request's, or a stale one carrying none — is a 500.

**The cluster half is ADR-118's.** The hop resolves `notifications-api:8081`
under Compose and in Kubernetes alike: there it is the Service of the API
host's own chart, `deploy/helm/notifications-api/`, which admits `web-bff` on
`grpc`, and `web-bff`'s policy reaches it there, the edge ADR-118 adds to
[ADR-065](adr/ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
table.

**Retention and erasure are ADR-117's**: a row not seen for
`DeviceTokenOptions.StaleAfter` is deleted, and an erasure clears every row of
the subject. The token is never a log attribute, a span attribute or part of
an exception's message ([§13.4](13-observability.md)).

### E.6.14 The notification preferences

[ADR-120](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)
decides that which messages a subject may be sent, on which channel, is a
journal of the subject's own acts, kept by the service that sends them, and
that the journal is the consent record. The client reads and writes it
through `Web.Bff`, which calls that service's API host as §10.10's
registration does: in the reference domain Notifications.Api
([E.5.4](appendix-e-reference-domain.md#e54-the-synchronous-hops)). What
follows is the wire contract.

| | Path |
|---|---|
| Client calls | `GET /bff/v1/notification-preferences` and `PUT /bff/v1/notification-preferences/{topic}/{channel}` |
| Gateway matches | `/bff/v1/{**catch-all}` |
| Service receives | `/v1/notification-preferences` and `/v1/notification-preferences/{topic}/{channel}` |

It needs no gateway change. A topic is one of the seven order events, by its
template key — `order-placed`, `order-confirmed`, `order-cancelled`,
`payment-declined`, `payment-refunded`, `shipment-dispatched` and
`shipment-delivered` — and a channel is `email` or `push`. The owning service's
`ConsentVocabulary` holds the list, and `PreferenceRules` is the BFF's check
against it. The subject is the caller's, as on
[§10.10](10-api-gateway.md#1010-a-write-forwarded-to-the-owning-service)'s routes.

**A `GET` reads every pair**, topic by topic in that order and each topic's
two channels, as `{ "preferences": [ … ] }`. A pair carries `topic`,
`channel`, `optedIn` and `source`: `required` for a pair the deployment is
obliged to send, `default` for one with no entry, and `set` for one the
subject set. A set pair alone carries `setAt` and `version`, the id of its
newest entry; any other carries both as `null`. A required pair is always
opted in, and the read says so rather than offering a switch that would do
nothing.

**A `PUT` sets one pair**, with a body of `{ "optedIn": <bool> }` and one
precondition: `If-Match` carrying the pair's `version` as a strong entity tag,
`"<version>"`, or `If-None-Match: *` for a pair the read showed with none. It
answers 200 with the pair as it now stands, in the read's shape, and an `ETag`
carrying its new version. A write that lost a race, because another device's
write landed since this one's read, answers 412 with the `code`
`preference.precondition_failed` and a `preference` member holding the pair as
it now stands, so an opt-in made on one device never undoes a withdrawal made
on another
([§10.11](10-api-gateway.md#1011-a-versioned-write-under-a-precondition)). A
pair the deployment requires answers 422 with the `code`
`preference.required`.

**The BFF refuses a malformed request before the hop**, with 400: a topic or
channel outside the vocabulary, and a body without `optedIn`, beside the
precondition shapes §10.11 lists. `PreconditionFilter` reads the two headers.

**The hop is `NotificationsHop`**, which registers the preferences client on
the same numbers as the devices client with its own breaker, and
`CallerTokenHandler` copies the inbound `Authorization` header unchanged. A
reply `preferences.proto` forbids — an outcome or a source left unset, a set
pair without its version and time, another pair with either, a pair outside the
vocabulary, or a write applied to a pair or value other than the one asked — is
a 500.

**Retention and erasure are ADR-120's**: an entry no longer current is kept for
`NotificationsJurisdictionOptions.ConsentRetention` and for as long as a kept
send names it or the entry it ended, and an erasure anonymises every entry of
the subject and keeps it.

## E.7 §12: the test strategy

This section holds what [§12](12-test-strategy.md) no longer states: which
suites the reference domain keeps its cases in, and the cases the chapter
compares a rule against. The chapter owns the rules and the shape of each
sample; every project, file and test below is one service's, in the reference
domain, and an adopter replaces it with their own.

### E.7.1 The journey and the outbound hops

`src/Services/` is the roster of what the journey walks. The journey of
[ADR-093](adr/ADR-093-the-journey-test-walks-one-order-across-the-services-and-asserts-that-they-converge.md)
walks one order across Catalog, Ordering, Inventory, Payments, Shipping and
Notifications, each the real host under its own broker account.

The outbound-hop row of §12.1 has three suites in the reference domain:

| Suite | What it holds |
|---|---|
| `Web.Bff.Tests` | §9.7's pricing hop: the timeout hierarchy, the credential handler's position and the realm; and the registration hop of [ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md): its timeout hierarchy, and the caller's own token in place of a credential handler, against a stub Notifications on loopback |
| `Shipping.Worker.Tests` | The first two properties, for the address read of [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md), against a stub Ordering on loopback |
| `Notifications.Worker.Tests` | All three, for the contact read the same record adds: the first two against a stub Keycloak on loopback, and the realm against a real one, which is also the owner the read asks |

### E.7.2 The fixture and the suites it serves

Ordering's domain tests are in `tests/Ordering.Domain.Tests/OrderTests.cs`,
which builds its orders with its own `AnOrder` helper under other test names
than §12.2's and §12.3's samples use; `OrderBuilder` is not in the tree.

Ordering's fixture is `tests/Ordering.TestSupport/ServiceFixture.cs`, derived
from the shared body, and the host it starts is `OrderingApiFactory` beside
it. Ordering calls no peer and never binds `ServiceIdentityOptions` (§9.7), so
its factory is given no `Identity:Client`. Its test scheme is
`tests/Ordering.TestSupport/TestAuthHandler.cs`.

The suites that use them, all under `tests/`:

| Project | File | Holds |
|---|---|---|
| `Ordering.Api.Tests` | `PlaceOrderTests.cs` | The place-order handler suite, over HTTP because the handler binds a subject; prices are seeded straight into `ordering.ProductPrices` |
| `Ordering.Api.Tests` | `OutboxDispatcherTests.cs` | The dispatcher's per-row isolation and its attempt cap, beside the unhandled-row test |
| `Ordering.Application.Tests` | `SagaContracts.cs` | The contract builders of the saga suite |
| `Ordering.Application.Tests` | `OutboxSerialisationTests.cs` | The `Local` lane's payload contract (§9.4) |
| `Ordering.TestSupport` | `Outbox/OutboxRows.cs` | The builders the dispatcher tests use |

### E.7.3 Where the handler tests live

Catalog is the only service whose two suites share §12.4's fixture: both take
it, which is why `*.TestSupport` exists. The other services' Application suites
reference `Common.TestSupport` alone.

Ordering departs from it for a reason that is the code's rather than the
chapter's.
Its handler tests live in `Ordering.Api.Tests`, because `ICurrentUser` is
`HttpContextCurrentUser` and a handler resolved in a bare scope has no
principal to bind a subject from. So `Ordering.Application.Tests` references
no `Ordering.TestSupport`, declares no fixture collection, and holds §12.5's
saga suite instead, which needs no infrastructure at all; its one collection,
`OrderFulfilmentSagaCollection`, carries no fixture and keeps that suite's
buses from running side by side. `Ordering.TestSupport` has one consumer there
rather than two. `docs/testing.md` says the same thing from the other side.

### E.7.4 The order summary cases

The cases of §12.4's order summary section are owed by Ordering's escalated
history query, over the `ordering.ProductPrices` and `ordering.Products`
tables, and the products they resolve are Catalog's: each resolved
`SummaryProduct` carries Catalog's name and its thumbnail. `ordering.Products`
and that query are not built ([§6.6](06-cqrs.md)); only the price half exists. `ProductPublished.ThumbnailUrl` is `string?`, which is the
nullable shape Catalog promises, and the late-arrival case rests on it.

All of the cases will belong in `Ordering.Api.Tests`. `Ordering.Application.Tests`
deliberately carries no `Ordering.TestSupport` reference and says so in its
csproj, so Ordering's real-schema tests are homed one project over;
`ProductPriceProjectionTests` is already there for exactly this reason.

### E.7.5 The ownership and subject suites

The cancel endpoint's suite is `tests/Ordering.Api.Tests/OrderOwnershipTests.cs`.

The subject rule's tests are in `PlaceOrderTests` (the attributed order) and
`OrderOwnershipTests` (the owner cancelling their own order), and the dispatcher
pair is in `SagaCommandHandlerTests`. A customer reads only their own orders
has no Ordering test, because Ordering's own history query, §6.5's as §6.6
escalates it, is not built. The buyer's history the BFF serves is under the
same rule ([§10.7](10-api-gateway.md)), and `Web.Bff.Tests`' `OrderEndpointTests`
asserts a stranger's page is empty.

The mapper tests are in `tests/Ordering.Application.Tests/CommandMapperTests.cs`.

### E.7.6 The saga suite

The suite is the `OrderFulfilmentSaga*Tests` classes in
`tests/Ordering.Application.Tests/`, mostly one per state, over the
registration they share in `OrderFulfilmentSagaHarness.cs`.

### E.7.7 The contract discovery

The domain assembly that §12.6's `No_contract_names_a_domain_type` keeps out
of a consumer is, for the order contracts, `Ordering.Domain`.

### E.7.8 The pricing hop and the address read

The pricing hop of §9.7 is the BFF's call to Catalog. The BFF's
`Web.Bff.Tests` drive its endpoint against `StubCatalog`, a hand-written gRPC
server modelling Catalog, whose drift from the real service is of four
kinds: filtering currency case-sensitively where Catalog does not, echoing
the request's spelling of the currency, formatting amounts at the test's
scale rather than the column's `decimal(19,4)`, and enforcing no request
ceiling at all.

- **The consumer's guard.** `CheckoutEndpoints` compares a reply's currency to
  the request's with `OrdinalIgnoreCase`, because Catalog answers `GBP` to a
  request for `gbp`.
- **The provider's side.** `PricingContract.cs` is linked into
  `Catalog.Api.Tests`, exactly as `pricing.proto` is linked into `Web.Bff`.
- **The amount's scale.** Catalog's column scale reaches the wire as
  `49.9900`.
- **What the contract leaves to the provider.** Catalog also refuses a
  malformed product id, a non-canonical GUID and an anonymous caller, which
  stay `PricingServiceTests`'.
- **The request ceiling.** `CheckoutEndpoints` holds no copy of Catalog's
  `MaxProductIds`. The quote bounds its own line count at
  `OrderLimits.MaxLines`, not a copy of Catalog's, so a basket past the
  ceiling fails validation before the hop and Catalog is never asked; the
  provider verification holds Catalog to the refusal either way, and the
  failure to watch for is a quote that refuses baskets Catalog would have
  priced, or accepts ones it would not.

The address read of ADR-052 is Shipping's worker asking Ordering, and
Ordering's API suite pins each of the statuses the worker tells apart against
the real provider. `StubOrdering` is still a hand-written stub.

The registration hop of
[ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)
is the BFF asking Notifications.Api, and `StubNotifications` stands in for it
in `Web.Bff.Tests`. It applies every write unless told otherwise and models
none of the ordering, because the BFF computes nothing over a reply: it maps
the outcome to a status, checks the sequence against the one it sent, and
relays a stale reply's sequence. Notifications' `DeviceRegistrationTests` pins
each outcome and its sequence against the real store, and `DevicesService`
maps them onto the reply one to one.

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
- *Event end-to-end in the load run (§13.7).* The run's orders publish
  `OrderPlaced` and `OrderConfirmed`, which Payments, Notifications, Shipping
  and the BFF consume through `IntegrationEventConsumer<T>`, so
  `deploy/observability/slo/slo.js` asserts the row over those two messages.

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
exists until §13.7's load run has a staging environment to run against, which
this repository does not have. It, `shipping.shipments.waiting` and
`shipping.carrier.unavailable` are read by `queue-backlog.md`'s procedure for a
worker whose wait is outside a consumer.

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

[§15](15-cicd-deployment.md) states each rule abstractly, with `<service>` and
`<Service>` standing for a service's lower-case and directory spellings. This
section holds the instances those placeholders stand for. Where a file already
holds a roster it is cited and not copied: the change-detection filters and the
image matrix are `.github/workflows/ci.yml`'s `changes` and `images` jobs, a
service's Dockerfiles sit beside its projects under `src/Services/`, its chart
is `deploy/helm/<service>/`, and the Compose units are `deploy/compose/`.

### E.9.1 The change filter and the proto-reaching filters

§15.1's sample filter is Ordering's: the shared anchor,
`src/Services/Ordering/**` and `tests/Ordering.*/**`. Every other service
repeats those three lines under its own key, and `.github/workflows/ci.yml`
holds the entry of each.

The two filters that reach into another service's tree:

| Filter | Extra path | Why |
|---|---|---|
| `bff` | `src/Services/Catalog/Catalog.Api/Protos/**` and `src/Services/Notifications/Notifications.Api/Protos/**` | Web.Bff compiles Catalog's `pricing.proto` and Notifications' `devices.proto` and `preferences.proto` as linked source files (§9.7) and its Dockerfile copies both paths |
| `shipping` | `src/Services/Ordering/Ordering.Api/Protos/**` | Shipping's images copy Ordering's `Protos/` for the delivery-address contract, on the BFF's terms |

`tests/Ordering.*/**` covers `Ordering.TestSupport` as well as Ordering's three
test projects, so Ordering's test helpers do not belong to `shared`, and
touching Catalog's test data builders redeploys Catalog alone.

### E.9.2 The images

§15.2's sample Dockerfile is `src/Services/Ordering/Ordering.Api/Dockerfile`,
whose entry point is `Ordering.Api.dll`, and its migrator's is
`src/Services/Ordering/Ordering.Migrator/Dockerfile`, whose entry point is
`Ordering.Migrator.dll`. The `images` job's matrix lists the pair for Ordering
as `filter: ordering` with the images `ordering-api` and `ordering-migrator`;
every other service has its own pair there, and Notifications, with two
hosts, its three (§15.2).

### E.9.3 The charts

§15.3's sample `values.yaml` is `deploy/helm/ordering/values.yaml`, the chart
of a service that has a database, a broker and Redis and calls no peer. Its
instances of the placeholders are:

| Value | Ordering's |
|---|---|
| `workload.name` | `ordering-api` |
| `image.api`, `image.migrator` | `ordering-api`, `ordering-migrator` |
| `database.connectionName` | `Ordering`, and `OrderingMigrator` in the hook |
| `database.runtimeSecretRef.name` | `ordering-database` |
| `database.migratorSecretRef.name` | `ordering-migrator-secret` |
| `broker.secretRef.name` | `ordering-rabbitmq` |
| `redis.secretRef.name` | `ordering-redis` |

Ordering calls no peer: Shipping reads its delivery address over the RPC
([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
its prices come from a local projection (§6.4) and the rest goes over the
broker, so its `identity.clientCredentials` is `false`.

The reference domain's workers are Shipping's and Notifications'. Each
consumes from the broker and exposes no API, and each chart,
`deploy/helm/shipping/` and `deploy/helm/notifications/`, writes
`service.enabled: false` and `ingress.enabled: false` down.
`deploy/helm/shipping/values.yaml` is the instance of §15.3's worker fence.
Against Ordering, a worker differs by `service.enabled` alone among those two
keys. The Ingress is the gateway's alone; every API host and the BFF are
reached through the edge (§10.1, §10.2), and Notifications' API host through
the BFF.

Notifications' second host has its own chart, `deploy/helm/notifications-api/`
([ADR-118](adr/ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)):
a Service on `http` for the probes and `grpc` for the BFF's hop (§10.10,
§10.11), an autoscaler as Catalog's, and the worker's database under the
worker's `connectionName` and runtime Secret, with no migrator image and no
Job. Its descriptor's `migratedBy` is `notifications`, the worker's release,
so the two roll in lockstep
([ADR-122](adr/ADR-122-notifications-two-hosts-roll-in-lockstep-so-a-migrations-previous-release-is-the-one-both-hosts-run-1038.md)).
Its fence admits `web-bff` on `grpc` and reaches, beyond the DNS and
telemetry every host reaches, the identity provider and the database; it
holds no broker account and no grant.

Each worker's chart sets `autoscaling.enabled: false` and `replicaCount: 3`,
three for availability across a node drain. What says three is too few is
`shipping.shipments.overdue` for Shipping and `notifications.overdue` for
Notifications. The queue-backlog rule of §13.6 watches each service's receive
endpoint, and each one's consumers only write a row: a `Pending` shipment or a
cancellation for Shipping, a `Pending` notification or an order record for
Notifications. The replica-bound work is the workers': Shipping's fulfilment
and tracking passes, and Notifications' send and push send workers. Shipping's
gauge is the wait of the rows a pass is due to claim and has not, by pass;
Notifications' is that wait over its notices and its push deliveries at once,
and `notifications.waiting`, by the step a row waits on, is the dependency's
wait beside it rather than the pass's. For Shipping and Notifications alike,
the backlog procedure of [`queue-backlog.md`](../runbooks/queue-backlog.md) is
the cue
([ADR-063](adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)),
and the ceiling is the carrier's, the relay's or a push provider's rate limit
rather than the queue's depth.

### E.9.4 The options a worker binds

`Shipping.Worker` and `Notifications.Worker` each register `CachingTokenClient`
and `ClientCredentialsHandler` in their own `Program.cs`, because each calls out
under a grant of its own. Without the binding, Web.Bff would request a token
with an empty scope and get 401s it would read as Catalog's fault.

The options types that earn a binding in the reference domain beyond
`Identity:Client`: `Mail` holds Notifications' relay and its credential,
`Push` its apps, and `Fcm` and `Apns` each push provider and its signing key,
`Jurisdiction` holds the statutory windows and, for Notifications, the
language set and the time zone, and `Fulfilment` (Shipping) and `Delivery`
(Notifications) each hold the give-up age of
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md).
The tempting counter-example is a `ServiceOptions`-shaped bag of batch sizes,
poll intervals and retry caps bound to an `Ordering` section that no
environment ever sets.

### E.9.5 The host-specific keys

§15.4's inventory holds the keys every host shares, the gateway's own and the
identity grant of a host that calls out. These are the keys one service's code
reads and §15.4 does not list, in the same columns. A key joins a chart when its host's
code reads it, and the host refuses to start without a required one.

| Key | Kind | Source | Required |
|---|---|---|---|
| `PaymentProvider__BaseUrl` | Config | ConfigMap | ✓ — **Payments only**; the provider's address, and the host refuses to start without it |
| `PaymentProvider__ApiKey` | Secret | External Secrets | ✓ — **Payments only**; the provider's credential, and the host refuses to start without it |
| `Carrier__BaseUrl` | Config | Helm `carrier.baseUrl` → ConfigMap | ✓ — **Shipping only**; the carrier's address, and the host refuses to start without it |
| `Carrier__ApiKey` | Secret | Helm `carrier.apiKeySecretRef` → External Secrets | ✓ — **Shipping only**; the carrier's credential, and the host refuses to start without it |
| `AddressSource__BaseUrl` | Config | Helm `addressSource.baseUrl` → ConfigMap | ✓ — **Shipping only**; the owner's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s read, and the host refuses to start without it |
| `Jurisdiction__AddressRetention` | Config | Helm `jurisdiction.addressRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a delivery address, and the host refuses to start without it |
| `Jurisdiction__TrackingRetention` | Config | Helm `jurisdiction.trackingRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a shipment's tracking events, and the host refuses to start without it |
| `Fulfilment__GiveUpAge` | Config | Helm `fulfilment.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Shipping only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a pending shipment, and [ADR-054](adr/ADR-054-a-shipment-stops-waiting-on-its-carrier-at-an-age.md)'s for an unanswered cancellation; the host refuses to start without it |
| `Privacy__Responders__0…n` | Config | Helm `privacy.responders` → ConfigMap | ✓ — **Privacy only**; [ADR-092](adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s holders, read once when a request is raised and stored on it, and the host refuses to start without one, on a repeated name, or on one that is not a lower-case name of letters, digits and hyphens |
| `Privacy__CompletionSlo` | Config | Helm `privacy.completionSlo` → ConfigMap | ✓ — **Privacy only**; how long a request may stay open before it is overdue, the adopter's reading of the law (ADR-053), and the host refuses to start without it |
| `Mail__Host` | Config | Helm `mail.host` → ConfigMap | ✓ — **Notifications only**; the relay's host name, and the host refuses to start without it |
| `Mail__Port` | Config | Helm `mail.port` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the relay's submission port |
| `Mail__From` | Config | Helm `mail.from` → ConfigMap | ✓ — **Notifications only**; the one sender every message carries, and the domain each `Message-ID` is minted under |
| `Mail__Security` | Config | Helm `mail.security` → ConfigMap, which admits `StartTls` alone | ✓ — **Notifications only**; `StartTls` or `None`, and `None` refuses to start outside Development |
| `Mail__UserName` | Config | Helm `mail.userName` → ConfigMap | ✓ **outside Development** — **Notifications only**; set with the password or not at all |
| `Mail__Password` | Secret | Helm `mail.passwordSecretRef` → External Secrets | ✓ **outside Development** — **Notifications only**; the relay's credential, and absent in Compose, where the sink takes unauthenticated submission |
| `ProductImages__Store__Endpoint`, `__Bucket`, `__Region`, `__PathStyle` | Config | Helm `objectStore.*` → ConfigMap | ✓ — **Web.Bff and Catalog only**; the object store of [ADR-106](adr/ADR-106-an-s3-compatible-object-store-for-product-images-the-bff-issues-a-pre-signed-upload-url-472.md), refused at start when missing. The BFF's endpoint is the address a client reaches, since it signs the URL the client uploads to, and Catalog's is the one that service reaches |
| `ProductImages__Store__PublicBaseUrl` | Config | Helm `objectStore.publicBaseUrl` → ConfigMap | ✓ — **Catalog only**; where `products/` is served from, the absolute base every stored image URL is built from (its scheme is [E.6.11](#e611-the-product-image-upload)'s rule), and short enough that it and the longest destination key fit `ThumbnailUrl` ([E.6.11](#e611-the-product-image-upload)) |
| `Push__Apps__0…n__AppId`, `__Platform` | Config | Helm `push.apps` → ConfigMap, empty in both charts | — **Web.Bff and Notifications' worker**; the worker registers a provider for each platform the list names, and writes a push notice beside each mail notice only once it names an app, since no device row can exist before; the two charts state the same list. The rollout refuses the notifications release while the list names an app and either track serves a revision from before push deliveries landed, whose workers claim a push notice as mail ([ADR-126](adr/ADR-126-the-rollout-keeps-a-values-path-empty-while-either-track-serves-a-revision-older-than-the-one-its-descriptor-names-1059.md)), so the first app is named once a release from push deliveries onward is the stable track. Each entry is one of [ADR-117](adr/ADR-117-a-devices-push-token-is-a-contact-notifications-owns-registered-through-the-bff-and-erased-with-its-subject-474.md)'s apps, each an app id and its platform, which a registration must match (§10.10). The host refuses an app id named twice or a platform outside `fcm` and `apns` at start, and an empty list refuses every registration |
| `ProductImages__Store__AccessKey`, `__SecretKey` | Secret | Helm `objectStore.secretRef` → External Secrets | ✓ — **Web.Bff and Catalog only**; one pair per host, never shared: the BFF's writes `uploads/` and nothing else, and Catalog's reads `uploads/` and writes `products/` (§10.8) |
| `ContactSource__BaseUrl` | Config | Helm `contactSource.baseUrl` → ConfigMap | ✓ — **Notifications only**; Keycloak's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s contact read, HTTPS outside Development, and the host refuses to start without it |
| `ContactSource__Realm` | Config | Helm `contactSource.realm` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the realm whose users are read, the one that issues this host's token, and the host refuses to start without it |
| `Jurisdiction__Languages__0…n` | Config | Helm `jurisdiction.languages` → ConfigMap | ✓ — **Notifications only**; ADR-053's language set, in the order a message in every language shows them; the host refuses to start unless every language has every template and a culture |
| `Jurisdiction__TimeZone` | Config | Helm `jurisdiction.timeZone` → ConfigMap | ✓ — **Notifications only**; the IANA zone a customer's dates are rendered in, resolved at start, so a zone the image does not know refuses the host |
| `Jurisdiction__LogRetention` | Config | Helm `jurisdiction.logRetention` → ConfigMap | ✓ — **Notifications only**; ADR-053 rule 4's statutory window for the record of a send, and the host refuses to start without it |
| `Jurisdiction__ContactRetention` | Config | Helm `jurisdiction.contactRetention` → ConfigMap | ✓ — **Notifications only**; ADR-052's contact row's window, refused at start when shorter than `ContactOptions.StaleCeiling` |
| `Jurisdiction__OrderRetention` | Config | Helm `jurisdiction.orderRetention` → ConfigMap | ✓ — **Notifications only**; the order record's window, and the host refuses to start without it |
| `Jurisdiction__ConsentRetention` | Config | Helm `jurisdiction.consentRetention` → ConfigMap | ✓ — **Notifications' worker only**; [ADR-120](adr/ADR-120-channel-preferences-are-a-consent-journal-notifications-keeps-per-subject-topic-and-channel-and-an-erasure-anonymises-it-474.md)'s statutory window for a consent entry once it stops being current, kept longer while a kept send names it or the entry it ended, and the host refuses to start without it |
| `Jurisdiction__Required__0…n` | Config | Helm `jurisdiction.required` → ConfigMap, in both Notifications charts | ✓ **in the chart** — **Notifications only, both hosts**; ADR-120's topic/channel pairs the deployment is obliged to send, which take no preference. The charts refuse it unstated and take `[]` as none; a host reads it absent as none, since configuration binds no key for an empty list, and refuses a pair outside the vocabulary or the service class at start. The reference values require the email of all seven topics |
| `Fcm__BaseUrl`, `__TokenUri` | Config | Helm `fcm.baseUrl`, `fcm.tokenUri` → ConfigMap, defaulted in the chart to Google's | ✓ **while an `fcm` app is named** — **Notifications' worker only**; FCM's API and the token endpoint the signed assertion is exchanged at, HTTPS outside Development |
| `Fcm__ProjectId`, `__ClientEmail` | Config | Helm `fcm.projectId`, `fcm.clientEmail` → ConfigMap | ✓ **while an `fcm` app is named** — **Notifications' worker only**; the Firebase project sent to and the service account the assertion is issued by |
| `Fcm__PrivateKey` | Secret | Helm `fcm.privateKeySecretRef` → External Secrets | ✓ **outside Development, while an `fcm` app is named** — **Notifications' worker only**; the service account's RSA key, generated by the worker in Development and absent in Compose |
| `Apns__BaseUrl` | Config | Helm `apns.baseUrl` → ConfigMap, defaulted in the chart to Apple's production host | ✓ **while an `apns` app is named** — **Notifications' worker only**; HTTPS and HTTP/2 outside Development |
| `Apns__TeamId`, `__KeyId` | Config | Helm `apns.teamId`, `apns.keyId` → ConfigMap | ✓ **while an `apns` app is named** — **Notifications' worker only**; the provider token's issuer and its signing key's id |
| `Apns__PrivateKey` | Secret | Helm `apns.privateKeySecretRef` → External Secrets | ✓ **outside Development, while an `apns` app is named** — **Notifications' worker only**; the team's P-256 signing key, generated by the worker in Development and absent in Compose |
| `Delivery__GiveUpAge` | Config | Helm `delivery.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a notification still pending, past which it is undeliverable with the reason `NotificationReasons.GaveUp`; the host refuses to start without it, or with one longer than `RetentionPolicy.InboxWindow` |

---

[← Appendix D](appendix-d-type-inventory.md) · [Index](README.md)
