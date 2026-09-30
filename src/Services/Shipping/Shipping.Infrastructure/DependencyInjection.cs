using Shipping.Application.Addresses;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Idempotency;
using Shipping.Infrastructure.Messaging;
using Shipping.Infrastructure.Observability;
using Shipping.Infrastructure.Persistence;
using Shipping.Infrastructure.Retention;
using Shipping.Infrastructure.Tracking;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.1's runtime identity, no DDL; EnableRetryOnFailure makes §6.3's CreateExecutionStrategy a real retry.
        services.AddDbContext<ShippingDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Shipping"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. One instance, not AddScoped<DbContext, ShippingDbContext>(), which
        // would commit the inbox row in a second context's own transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<ShippingDbContext>());

        // Each layer scans itself (§6.2); scanning only Application would skip this layer's command mappers.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3

        // §5.6's repository for §3.2's aggregate.
        services.AddScoped<IShipmentRepository, ShipmentRepository>();

        // §8.5's durable half, in EfUnitOfWork's transaction through the alias above. A missing line fails the
        // first command, not startup: ValidateOnBuild never constructs TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §7.5's two halves, scoped because the context is.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // Values, since Common.Infrastructure is every service's; one local, so no two tables name different schemas.
        const string schema = "shipping";
        services.AddSingleton(new OutboxTable(schema));
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // §9.4's, §9.5's and §8.5's retention windows, registered rather than const so the service can change them.
        services.AddSingleton(new RetentionPolicy());

        // §9.4's persisted type names; the source is separate so a test host can add its own assembly.
        // The map is lazy, so MessageTypeMapValidator is what fails the host, not the first message, on a duplicate.
        services.AddSingleton(
            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(Shipment).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // §9.4's payload format. A value object on a domain event needs a converter here: a readonly record
        // struct deserialises to its default rather than failing, which §12.4's round trip catches.
        services.AddSingleton<OutboxJson>();

        // §13.3's messaging instruments.
        services.AddSingleton<MessagingMetrics>();

        // §13.6's outbox gauges, singletons so one meter holds one set of instruments. OutboxStats runs in gauge
        // callbacks, so it gets the runtime key (§7.1) with its own bounded connect timeout, which no query inherits.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Shipping"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
            new SqlConnectionFactory(metricsConnectionString),
            sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // §13.6's shipment gauges, on the same bounded connection for OutboxStats' reason. Through a factory, so
        // the container disposes the stats it constructed; it never disposes an instance it was handed.
        services.AddSingleton<IShipmentStats>(
            _ => new ShipmentStats(new SqlConnectionFactory(metricsConnectionString)));
        services.AddSingleton<ShipmentMetrics>();

        // Constructs the metrics singletons at start, before the bus, so they exist for the first message (§13.6).
        services.AddHostedService<MetricsInitialiser>();

        // §2: no Redis, yet RetentionPurgeService resolves IIdempotencyStore for ADR-039's marker purge.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();

        // The bus (§9); AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // §9.4's poll loop. The generic overload records the ImplementationType §12.4's fixture removes it by.
        // After the bus, since hosted services stop in reverse and the dispatcher drains into a live transport.
        services.AddHostedService<OutboxDispatcher>();

        // The fulfilment pass; the generic overload, so a suite can remove it by its ImplementationType (§12.4).
        services.AddScoped<FulfilmentClaims>();
        services.AddHostedService<FulfilmentWorker>();

        // ADR-052's give-up age, bound beside its consumer (§15.4); a missing or impossible one refuses the host.
        services
            .AddOptions<FulfilmentOptions>()
            .BindConfiguration(FulfilmentOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FulfilmentOptions>, AnnotatedOptionsValidator<FulfilmentOptions>>();

        // The tracking pass, registered by its implementation type for the fulfilment worker's reason (§12.4).
        services.AddScoped<TrackingClaims>();
        services.AddHostedService<TrackingWorker>();

        // ADR-053's windows, bound beside ShippingRetentionService (§15.4); a missing or impossible one refuses
        // the host at start.
        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<ShippingJurisdictionOptions>,
            AnnotatedOptionsValidator<ShippingJurisdictionOptions>>();

        // By implementation type, which §12.4's fixture removes it by, so its start-up pass never races a seed.
        services.AddHostedService<ShippingRetentionService>();

        // §9.4's, §9.5's and §8.5's retention. Last, so first stopped: an interrupted purge loses nothing.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side, singleton as §4.2's sample has it, on the runtime key rather than the migrator's (§7.1).
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Shipping")!));

        // ADR-052's contact row, in a table of its own beside the shipment.
        services.AddScoped<IDeliveryAddressStore, SqlDeliveryAddressStore>();

        // Readiness lives here, not in Common.Web, because it needs the connection string (§13.5).
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Shipping")!, name: "sql", tags: ["ready"]);

        return services;
    }
}
