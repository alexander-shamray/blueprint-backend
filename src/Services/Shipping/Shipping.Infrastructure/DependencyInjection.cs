using Shipping.Application.Addresses;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Idempotency;
using Shipping.Infrastructure.Messaging;
using Shipping.Infrastructure.Observability;
using Shipping.Infrastructure.Persistence;
using Shipping.Infrastructure.Retention;
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

/// <summary>
/// The one registration method this layer exposes (§4.2), and the assembly's
/// <c>typeof</c> anchor.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.1's runtime identity — data plane only, no DDL. The migrator key
        // is deliberately unreadable from here: two connection strings that any
        // host may read are a naming convention, not a boundary.
        // EnableRetryOnFailure is what makes §6.3's CreateExecutionStrategy a
        // real retry rather than a no-op.
        services.AddDbContext<ShippingDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Shipping"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter is common code and names DbContext, not the
        // derived type; this alias is what makes that legal. GetRequiredService,
        // not AddScoped<DbContext, ShippingDbContext>(): the second form builds
        // a second context in the same scope, so the inbox row would commit in
        // its own transaction and §9.5's atomic-with-the-handler guarantee
        // would silently stop holding. Both resolutions must be one instance.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<ShippingDbContext>());

        // Each layer scans itself (§6.2): this layer's projections, cache
        // invalidators and command mappers belong here, and scanning only
        // Application would skip them.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3

        // §5.6's repository for §3.2's aggregate.
        services.AddScoped<IShipmentRepository, ShipmentRepository>();

        // §8.5's durable half. Only this one has to land on the transaction
        // EfUnitOfWork opens — it resolves the DbContext alias above, which is
        // what puts the marker in that transaction. Losing this line surfaces
        // on the first command rather than at startup, because ValidateOnBuild
        // never constructs TransactionBehavior's open generic.
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
        const string schema = "shipping";
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
        // §9.4's two anchors: IIntegrationEvent until this service publishes
        // a contract of its own (§9.3), and the aggregate for its domain.
        services.AddSingleton(
            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(Shipment).Assembly));
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

        // §13.6's per-lane outbox gauges, and the stats type behind them. Both
        // singletons: the gauges are callbacks the Meter holds, and a second
        // instance would mean two sets of instruments on one meter.
        //
        // OutboxStats gets its own connection factory with the bounded connect
        // timeout its own constant argues, because it runs inside gauge
        // callbacks and a command timeout bounds only the statement. The
        // runtime key, because it reads the same data plane (§7.1); only the
        // timeout differs, so no query path inherits it.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Shipping"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
            new SqlConnectionFactory(metricsConnectionString),
            sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Singleton registration alone is lazy: instruments appear on first
        // resolve, which for a class nothing injects is never, and
        // ValidateOnBuild cannot check it because nothing depends on a metrics
        // class (§6.2). Registered before the bus and the dispatcher, so the
        // instruments exist before the first message is delivered against them.
        services.AddHostedService<MetricsInitialiser>();

        // §2: no Redis. RetentionPurgeService still resolves IIdempotencyStore
        // unconditionally for ADR-039's marker purge, so this service registers
        // its own rather than the shared Redis-backed one it has no connection
        // for.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();

        // The bus (§9). Its readiness needs no line below: AddMassTransit
        // registers the bus health check itself — "masstransit-bus", tagged
        // ready — argued at the registration.
        services.AddMassTransitMessaging(configuration);

        // The poll loop of §9.4. AddHostedService<T>, not a factory over a
        // registered singleton: the generic overload records an
        // ImplementationType, which is what §12.4's fixture matches on to
        // remove only this hosted service without also removing MassTransit's
        // bus, itself a hosted service RemoveAll<IHostedService>() would stop.
        //
        // Registered after the bus and before the purge: hosted services stop
        // in reverse, so the dispatcher drains into a transport still up, and
        // registering it before the bus would let a deploy stop the broker
        // underneath a dispatcher still claiming rows.
        services.AddHostedService<OutboxDispatcher>();

        // Spec section 4's first worker. AddHostedService<T> rather than a
        // factory overload, so a suite that drives one pass can find and
        // remove exactly this registration by its implementation type.
        services.AddScoped<FulfilmentClaims>();
        services.AddHostedService<FulfilmentWorker>();

        // §15.4's shape, in the registration helper that owns the consumer
        // rather than in Program.cs: a binding hoisted upwards re-imposes the
        // key on every host, which is the mistake that section spends a
        // paragraph on. ValidateOnStart is what turns a missing statutory
        // window into a refusal to boot — IOptions<T> always resolves, so
        // without it the purge would run with a default-constructed instance
        // and delete nothing while reporting healthy (ADR-053).
        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ShippingJurisdictionOptions>, ShippingJurisdictionOptionsValidator>();

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
            new SqlConnectionFactory(configuration.GetConnectionString("Shipping")!));

        // A table of its own beside the shipment (spec, section 7): scoped
        // only because AddScoped is this layer's default for a port, not
        // because the store holds any per-request state.
        services.AddScoped<IDeliveryAddressStore, SqlDeliveryAddressStore>();

        // Readiness lives here, not in Common.Web, because it needs the
        // connection string the shared host package does not have (§13.5).
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Shipping")!, name: "sql", tags: ["ready"]);

        return services;
    }
}
