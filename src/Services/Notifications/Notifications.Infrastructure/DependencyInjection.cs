using Notifications.Application.Contacts;
using Notifications.Application.Records;
using Notifications.Infrastructure.Idempotency;
using Notifications.Infrastructure.Messaging;
using Notifications.Infrastructure.Observability;
using Notifications.Infrastructure.Persistence;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.Infrastructure;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // §7.1's runtime key, data plane only. EnableRetryOnFailure is what makes §6.3's
        // CreateExecutionStrategy a real retry rather than a no-op.
        services.AddDbContext<NotificationsDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Notifications"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. An alias, not AddScoped<DbContext, NotificationsDbContext>(): a second
        // context would commit the inbox row outside the handler's transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<NotificationsDbContext>());

        // Each layer scans itself (§6.2).
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3

        // §6.3's repositories: the notices owed, and the order record four of them wait on for a customer.
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IOrderRecordRepository, OrderRecordRepository>();

        // §8.5's durable half, on the DbContext alias above and so in EfUnitOfWork's transaction. Its loss
        // fails the first command, not startup: ValidateOnBuild never builds TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §2: no Redis. The purge still asks the claim store (ADR-039), so this one never claims.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();

        // One local, so the tables of §9.5 and §8.5 cannot name different schemas; there is no outbox (§3.2).
        const string schema = "notifications";
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // Registered rather than const: §9.5 says to check the inbox window against the broker's redelivery limits.
        services.AddSingleton(new RetentionPolicy());

        // §13.3's messaging instruments; the class owns the list.
        services.AddSingleton<MessagingMetrics>();

        // Resolves the metrics classes at start, before the bus, so every instrument exists before the first
        // message (§13.6).
        services.AddHostedService<MetricsInitialiser>();

        // The bus (§9). AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // The one retention service §9.5 asks for, registered last so it is stopped first.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side, a singleton as §4.2's sample has it, on §7.1's runtime key.
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Notifications")!));

        // ADR-052's contact row, the one table here a mailbox lands in.
        services.AddScoped<IContactStore, SqlContactStore>();

        // Readiness (§13.5): SQL here, and the bus check AddMassTransit registers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Notifications")!, name: "sql", tags: ["ready"]);

        return services;
    }
}
