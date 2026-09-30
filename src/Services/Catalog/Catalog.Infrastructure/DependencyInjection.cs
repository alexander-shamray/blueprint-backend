using System.Text.Json.Serialization;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Messaging;
using Catalog.Infrastructure.Observability;
using Catalog.Infrastructure.Persistence;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Infrastructure.Redis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.1's runtime key, data plane only. EnableRetryOnFailure is what makes §6.3's
        // CreateExecutionStrategy a real retry rather than a no-op.
        services.AddDbContext<CatalogDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Catalog"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. An alias, not AddScoped<DbContext, CatalogDbContext>(): a second
        // context would commit the inbox row outside the handler's transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<CatalogDbContext>());

        // Each layer scans itself (§6.2).
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3
        services.AddScoped<IProductRepository, ProductRepository>();         // §5.6

        // §8.5's durable half, on the DbContext alias above and so in EfUnitOfWork's transaction. Its loss
        // fails the first command, not startup: ValidateOnBuild never builds TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §7.5's two halves, scoped because the context they share is.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // One local, so the tables of §9.4, §9.5 and §8.5 cannot name different schemas.
        const string schema = "catalog";
        services.AddSingleton(new OutboxTable(schema));
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // Registered rather than const: §9.5 says to check the inbox window against the broker's redelivery limits.
        services.AddSingleton(new RetentionPolicy());

        // The persisted type names (§9.4). The map is built lazily, so MessageTypeMapValidator is what fails
        // the host on a duplicate FullName rather than the first message.
        services.AddSingleton(
            new MessageTypeSource(typeof(ProductPublished).Assembly, typeof(Product).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // The payload format (§9.4), and the converters that make this
        // service's value objects part of it. MoneyJsonConverter is the same
        // decision as ProductConfiguration's ComplexProperty: Money is
        // persisted twice, as two columns and as two JSON members, and knows
        // about neither. Its absence is silent — a Money round-trips to zero
        // and a null currency rather than throwing.
        services.AddSingleton<JsonConverter, MoneyJsonConverter>();
        services.AddSingleton<OutboxJson>();

        // §13.3's messaging instruments; the class owns the list.
        services.AddSingleton<MessagingMetrics>();

        // §13.6's per-lane gauges, singletons because the Meter holds their callbacks. OutboxStats gets its
        // own factory on the runtime key (§7.1), so its bounded connect timeout reaches no query path.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Catalog"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
            new SqlConnectionFactory(metricsConnectionString),
            sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Instruments appear on first resolve, and nothing injects a metrics class (§6.2); registered before the
        // bus and the dispatcher, so they exist before the first message.
        services.AddHostedService<MetricsInitialiser>();

        // §8's two connections, one call by design (§8.2). Both strings are read eagerly, so a missing key
        // stops the host.
        services.AddRedisConnections(configuration);

        // The bus (§9). AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // The poll loop of §9.4, by AddHostedService<T> because §12.4's fixture removes it by ImplementationType.
        // After the bus and before the purge: hosted services stop in reverse, so it drains into a live transport.
        services.AddHostedService<OutboxDispatcher>();

        // The one retention service §9.5 asks for, registered last so it is stopped first.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side, a singleton as §4.2's sample has it, on §7.1's runtime key.
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Catalog")!));

        // Readiness (§13.5). Both Redis rows: AbortOnConnectFail is false, and §8.1 puts the two instances on
        // different servers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Catalog")!, name: "sql", tags: ["ready"])
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
