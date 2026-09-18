using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Messaging;
using Inventory.Infrastructure.Persistence;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

/// <summary>
/// The one registration method this layer exposes (§4.2), and the assembly's
/// <c>typeof</c> anchor.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInventoryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.1's runtime identity — data plane only, no DDL. The migrator key
        // is deliberately unreadable from here: two connection strings that any
        // host may read are a naming convention, not a boundary.
        // EnableRetryOnFailure is what makes §6.3's CreateExecutionStrategy a
        // real retry rather than a no-op.
        services.AddDbContext<InventoryDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Inventory"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter is common code and names DbContext, not the
        // derived type; this alias is what makes that legal. GetRequiredService,
        // not AddScoped<DbContext, InventoryDbContext>(): the second form builds
        // a second context in the same scope, so the inbox row would commit in
        // its own transaction and §9.5's atomic-with-the-handler guarantee
        // would silently stop holding. Both resolutions must be one instance.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<InventoryDbContext>());

        // Each layer scans itself (§6.2): this layer's projections, cache
        // invalidators and command mappers belong here, and scanning only
        // Application would skip them.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3
        services.AddScoped<IStockItemRepository, StockItemRepository>();     // §5.6
        services.AddScoped<IReservationRepository, ReservationRepository>(); // §5.6
        services.AddScoped<IStockLedger, SqlStockLedger>();                  // §7.3

        // §8.5's durable half, beside the unit of work rather than in
        // AddRedisConnections with its Redis sibling: the two ports are backed
        // by different systems, and only this one has to land on the
        // transaction EfUnitOfWork opens — it resolves the DbContext alias
        // above, which is what puts the marker in that transaction. Losing
        // this line surfaces on the first command rather than at startup,
        // because ValidateOnBuild never constructs TransactionBehavior's open
        // generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §7.5's two Infrastructure halves: the collector reads EF's change
        // tracker, the publisher writes the row on the same context. Both
        // scoped, because the context is — a singleton either side would
        // stage into a transaction that had already closed.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // The schema the dispatcher's statements and the purge's are composed
        // against. Values rather than literals in Common.Infrastructure,
        // because that assembly is every service's (§9.4, §9.5, §8.5) — and
        // all built from one local, so no two of these tables can end up
        // naming different schemas.
        const string schema = "inventory";
        services.AddSingleton(new OutboxTable(schema));
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // §9.4's, §9.5's and §8.5's retention windows at their defaults.
        // Registered rather than const, because §9.5 tells the reader to check
        // the inbox window against the broker's redelivery limits, and a number
        // a chapter says to check has to be one the service can change.
        services.AddSingleton(new RetentionPolicy());

        // The persisted type names (§9.4). The source is registered separately
        // so a test host can add its own assembly without replacing the
        // production pair.
        //
        // The map's factory is lazy and nothing resolves it until the
        // dispatcher claims a row, so MessageTypeMapValidator is what makes a
        // duplicate FullName fail the host rather than the first message. It
        // is the first hosted service because hosted services start in order.
        // §9.4's two anchors are this service's contracts and its domain, as
        // Catalog's are typeof(ProductPublished).Assembly and typeof(Product).
        services.AddSingleton(
            new MessageTypeSource(typeof(StockLevelChanged).Assembly, typeof(StockItem).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // The payload format (§9.4). The first value object this service puts
        // on a domain event needs a converter registered here: a readonly
        // record struct deserialises to its default rather than failing,
        // and §12.4's round-trip assertion is what catches that.
        services.AddSingleton<OutboxJson>();

        // §13.3's messaging instruments, on the Commerce.Messaging meter
        // AddObservability already collects; the class owns the list.
        services.AddSingleton<MessagingMetrics>();

        // §8's two connections. The call brings §8.2's HybridCache stack with
        // it whether or not this service reads a cache, because the method is
        // one call by design (§8.2): a service either has Redis or does not,
        // and half-having it is the state that produces a cache silently
        // reading the database. Both connection strings are read eagerly and
        // throw when absent, so this line is what a missing key stops — the
        // host will not start.
        services.AddRedisConnections(configuration);

        // The bus (§9). Its readiness needs no line below: AddMassTransit
        // registers the bus health check itself — "masstransit-bus", tagged
        // ready — argued at the registration.
        services.AddMassTransitMessaging(configuration);

        // The poll loop of §9.4. AddHostedService<T>, not a factory over a
        // registered singleton: the generic overload records an
        // ImplementationType, which is what §12.4's fixture matches on to
        // remove only this hosted service — MassTransit's bus is one too, and
        // RemoveAll<IHostedService>() would stop the broker. A factory
        // registration leaves ImplementationType null and that removal
        // matches nothing.
        //
        // Registered after the bus and before the purge, and the order is a
        // shutdown decision: hosted services stop in reverse, so the
        // dispatcher stops while the transport it publishes through is still
        // up and drains into it. Registered before the bus, every deploy would
        // stop the broker underneath a dispatcher still claiming rows.
        services.AddHostedService<OutboxDispatcher>();

        // §9.4's, §9.5's and §8.5's retention, in the one hosted service §9.5
        // asks for. Registered last, so it is the first stopped: it is pure
        // housekeeping, and a deploy that interrupts a purge loses nothing an
        // hour will not redo.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side. Singleton, as §4.2's sample has it: the factory
        // holds a string and constructs per call, and the connections it hands
        // out are the caller's to dispose, so there is no scoped state to
        // capture. The runtime key, deliberately: a query on the migrator's
        // identity would be §7.1's boundary failing quietly.
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Inventory")!));

        // Readiness lives here, not in Common.Web, because it needs the
        // connection strings the shared host package does not have (§13.5).
        // The Redis rows are not optional: AbortOnConnectFail is false (§8.1),
        // so a disconnected multiplexer does not stop the host, and without
        // them it would sit Ready while every idempotency claim failed closed
        // — the case §13.5 says is indistinguishable from readiness never
        // having been wired. Both, not one: §8.1 gives the two instances
        // different eviction policies and therefore different servers, so a
        // cache that is up says nothing about the coordination instance §8.5
        // writes claims to.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Inventory")!, name: "sql", tags: ["ready"])
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
}
