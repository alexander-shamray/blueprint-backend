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
`<Service>.Api.Tests` (`<Service>.Worker.Tests` for a worker, and no
`<Service>.Domain.Tests` for a pure consumer, which has no Domain project),
with a `<Service>.TestSupport`, under `tests/`.

| Service | Host shape | Projects under `src/Services/<Service>/` |
|---|---|---|
| Catalog | API host | Domain, Application, Infrastructure, Migrator, Api |
| Ordering | API host | Domain, Application, Infrastructure, Migrator, Api |
| Inventory | API host | Domain, Application, Infrastructure, Migrator, Api |
| Payments | API host | Domain, Application, Infrastructure, Migrator, Api |
| Privacy | API host | Domain, Application, Infrastructure, Migrator, Api |
| Shipping | worker | Domain, Application, Infrastructure, Migrator, Worker |
| Notifications | pure consumer | Application, Infrastructure, Migrator, Worker |

Ordering's tree is the one §4.1 draws generically: its Application project
holds a `DependencyInjection.cs` with `AddOrderingApplication()`, its
Infrastructure project one with `AddOrderingInfrastructure(config)`, and
`Ordering.Api/Program.cs` is its only composition root.

Shipping and Notifications expose no public API but still own schemas:
`Shipment` and `TrackingEvent`, and `NotificationLog`, respectively
([§3.2](03-bounded-contexts.md)). `tools/new-service` refuses Shipping
without `--worker` and Notifications without `--pure-consumer`, so that it
does not render either in a shape §4.1 does not give it.

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

        // Explicit rather than scanned, beside the dispatcher it serves, as §4.2's sample has it (§7.5).
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

        // §4.2's sample line, anchored on a validator: losing the last one fails the build, not silently.
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

        // §6.5's read side, singleton as §4.2's sample has it, on the runtime key rather than the migrator's (§7.1).
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
- **The callee of the BFF's hop.** The stub that `Web.Bff.TestSupport` holds
  is Catalog's, with the server half of `pricing.proto`, and the
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

Filled by #908.

## E.4 §6: CQRS and its projections

Filled by #910.

## E.5 §9: messaging and the sagas

Filled by #909.

## E.6 §10 and §11: the gateway routes and the identity realms

Filled by #911.

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
| `Web.Bff.Tests` | §9.7's pricing hop: the timeout hierarchy, the credential handler's position and the realm |
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

## E.8 §13 and §14: observability and local development

Filled by #906.

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
| `bff` | `src/Services/Catalog/Catalog.Api/Protos/**` | Web.Bff compiles Catalog's `pricing.proto` as a linked source file (§9.7) and its Dockerfile copies that path |
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
every other service has its own pair there.

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

The reference domain's workers are Shipping and Notifications. Each consumes
from the broker and exposes no API, and each chart, `deploy/helm/shipping/` and
`deploy/helm/notifications/`, writes `service.enabled: false` and
`ingress.enabled: false` down. `deploy/helm/shipping/values.yaml` is the
instance of §15.3's worker fence. Against Ordering, a worker differs by
`service.enabled` alone among those two keys. The Ingress is the gateway's
alone; every API host and the BFF are reached through the edge
(§10.1, §10.2).

Each worker's chart sets `autoscaling.enabled: false` and `replicaCount: 3`,
three for availability across a node drain. What says three is too few is
`shipping.shipments.overdue` for Shipping and `notifications.overdue` for
Notifications. The queue-backlog rule of §13.6 watches each service's receive
endpoint, and each one's consumers only write a row: a `Pending` shipment or a
cancellation for Shipping, a `Pending` notification or an order record for
Notifications. The replica-bound work is the workers': Shipping's fulfilment
and tracking passes, and Notifications' send worker. Shipping's gauge is the
wait of the rows a pass is due to claim and has not, by pass; Notifications'
is that wait for its one pass, and `notifications.waiting`, by the step a row
waits on, is the dependency's wait beside it rather than the pass's. For
Shipping and Notifications alike, the backlog procedure of
[`queue-backlog.md`](../runbooks/queue-backlog.md) is the cue
([ADR-063](adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)),
and the ceiling is the carrier's or the relay's rate limit rather than the
queue's depth.

### E.9.4 The options a worker binds

`Shipping.Worker` and `Notifications.Worker` each register `CachingTokenClient`
and `ClientCredentialsHandler` in their own `Program.cs`, because each calls out
under a grant of its own. Without the binding, Web.Bff would request a token
with an empty scope and get 401s it would read as Catalog's fault.

The options types that earn a binding in the reference domain beyond
`Identity:Client`: `Mail` holds Notifications' relay and its credential,
`Jurisdiction` holds the statutory windows and, for Notifications, the
language set and the time zone, and `Fulfilment` (Shipping) and `Delivery`
(Notifications) each hold the give-up age of
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md).
The tempting counter-example is a `ServiceOptions`-shaped bag of batch sizes,
poll intervals and retry caps bound to an `Ordering` section that no
environment ever sets.

### E.9.5 The host-specific keys

§15.4's inventory holds the keys every host shares, the gateway's own and the
identity grant of a host that calls out. These are the keys one service's code reads and
§15.4 does not list, in the same columns. A key joins a chart when its host's
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
| `ContactSource__BaseUrl` | Config | Helm `contactSource.baseUrl` → ConfigMap | ✓ — **Notifications only**; Keycloak's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s contact read, HTTPS outside Development, and the host refuses to start without it |
| `ContactSource__Realm` | Config | Helm `contactSource.realm` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the realm whose users are read, the one that issues this host's token, and the host refuses to start without it |
| `Jurisdiction__Languages__0…n` | Config | Helm `jurisdiction.languages` → ConfigMap | ✓ — **Notifications only**; ADR-053's language set, in the order a message in every language shows them; the host refuses to start unless every language has every template and a culture |
| `Jurisdiction__TimeZone` | Config | Helm `jurisdiction.timeZone` → ConfigMap | ✓ — **Notifications only**; the IANA zone a customer's dates are rendered in, resolved at start, so a zone the image does not know refuses the host |
| `Jurisdiction__LogRetention` | Config | Helm `jurisdiction.logRetention` → ConfigMap | ✓ — **Notifications only**; ADR-053 rule 4's statutory window for the record of a send, and the host refuses to start without it |
| `Jurisdiction__ContactRetention` | Config | Helm `jurisdiction.contactRetention` → ConfigMap | ✓ — **Notifications only**; ADR-052's contact row's window, refused at start when shorter than `ContactOptions.StaleCeiling` |
| `Jurisdiction__OrderRetention` | Config | Helm `jurisdiction.orderRetention` → ConfigMap | ✓ — **Notifications only**; the order record's window, and the host refuses to start without it |
| `Delivery__GiveUpAge` | Config | Helm `delivery.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a notification still pending, past which it is undeliverable with the reason `NotificationReasons.GaveUp`; the host refuses to start without it, or with one longer than `RetentionPolicy.InboxWindow` |

---

[← Appendix D](appendix-d-type-inventory.md) · [Index](README.md)
