using System.Text.Json.Serialization;
using Ordering.Application.Orders;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Observability;
using Ordering.Infrastructure.Persistence;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ordering.Infrastructure;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
}
