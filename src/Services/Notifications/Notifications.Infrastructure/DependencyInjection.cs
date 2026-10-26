using Common.Infrastructure.Transport;
using Notifications.Application;
using Notifications.Application.Contacts;
using Notifications.Application.Devices;
using Notifications.Application.Preferences;
using Notifications.Application.Privacy;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.Infrastructure.Messaging;
using Notifications.Infrastructure.Observability;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Retention;
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Validation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    /// <param name="host">
    /// Which of the service's two hosts is composing (ADR-118): the API shares the database and none of the
    /// worker's consumers, loops or meters, so a second process never runs a second set of them.
    /// </param>
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        NotificationsHost host)
    {
        // ADR-079, before any connection below is opened.
        services.AddTransportSecurity();

        // §7.1's runtime key, data plane only. EnableRetryOnFailure is what makes §6.3's
        // CreateExecutionStrategy a real retry rather than a no-op.
        services.AddDbContext<NotificationsDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Notifications"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. An alias, not AddScoped<DbContext, NotificationsDbContext>(): a second
        // context would commit the inbox row outside the handler's transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<NotificationsDbContext>());

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3

        // §8.5's durable half, on the DbContext alias above and so in EfUnitOfWork's transaction, which both hosts'
        // commands run in. Its loss fails the first command, not startup: ValidateOnBuild never builds
        // TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §2: no Redis. The purge still asks the claim store (ADR-039), so this one never claims.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();

        // One local, so the tables of §9.5 and §8.5 cannot name different schemas; there is no outbox (§3.2).
        const string schema = "notifications";
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // ADR-117's device rows: the API writes them, and the worker's stale pass and erasure clear them.
        services.AddScoped<IDeviceStore, SqlDeviceStore>();

        // ADR-120's journal: the API appends and reads it, and the worker reads it at send time.
        services.AddScoped<IConsentJournal, SqlConsentJournal>();

        // §11.7's audit rows: the worker's erasure writes them, and the API's registrations and preference writes
        // stamp from them (ADR-138); shared, as both hosts scan the handlers that take it.
        services.AddScoped<IPersonalDataErasureRepository, PersonalDataErasureRepository>();

        // ADR-053's one options class, in both hosts since both read the required pairs; each host validates the
        // members it reads, so the API is not refused for the worker's windows (ADR-120).
        services
            .AddOptions<NotificationsJurisdictionOptions>()
            .BindConfiguration(NotificationsJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton(sp =>
            RequiredPairsRule.From(sp.GetRequiredService<IOptions<NotificationsJurisdictionOptions>>()));

        // §6.5's read side, a singleton as E.2.6's sample has it, on §7.1's runtime key.
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Notifications")!));

        // Readiness (§13.5): SQL for both hosts, and the worker's bus check AddMassTransit registers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Notifications")!, name: "sql", tags: ["ready"]);

        if (host == NotificationsHost.Api)
        {
            services.AddSingleton<IValidateOptions<NotificationsJurisdictionOptions>, RequiredPairsValidator>();
            return services;
        }

        return services.AddWorkerHalf(configuration);
    }

    private static IServiceCollection AddWorkerHalf(this IServiceCollection services, IConfiguration configuration)
    {
        // Each layer scans itself (§6.2).
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        // §5.6's repositories: the notices owed, and the order record four of them wait on for a customer.
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IOrderRecordRepository, OrderRecordRepository>();

        // The push design's deliveries, and the device rows the fan-out and the push pass read.
        services.AddScoped<IPushDeliveryRepository, PushDeliveryRepository>();
        services.AddScoped<PushDevices>();

        // §11.7's erasure: the deletes in the unit's transaction and the report after it (ADR-094).
        services.AddScoped<INotificationsPersonalDataStore, SqlNotificationsPersonalDataStore>();
        services.AddScoped<IErasedSubjects, SqlErasedSubjects>();
        services.AddScoped<IErasureReporter, ErasureReporter>();

        // Registered rather than const: §9.5 says to check the inbox window against the broker's redelivery limits.
        services.AddSingleton(new RetentionPolicy());

        // §13.3's messaging instruments; the class owns the list.
        services.AddSingleton<MessagingMetrics>();

        // Resolves the metrics classes at start, before the bus, so every instrument exists before the first
        // message (§13.6).
        services.AddHostedService<MetricsInitialiser>();

        // The bus (§9). AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // The whole of ADR-053's class, validated beside the renderer that reads its language set and zone (§15.4).
        services.AddSingleton<
            IValidateOptions<NotificationsJurisdictionOptions>,
            NotificationsJurisdictionValidator>();

        // Built from options the validator passed at start, so a refusal is the host's and never a send's.
        services.AddSingleton(sp =>
        {
            NotificationsJurisdictionOptions jurisdiction =
                sp.GetRequiredService<IOptions<NotificationsJurisdictionOptions>>().Value;

            return TemplateRenderer.Create(TemplateSet.Embedded, jurisdiction.Languages!, jurisdiction.TimeZone!);
        });

        // ADR-052's give-up age, bound beside its worker (§15.4); a missing, impossible or unguarded one refuses the
        // host at start.
        services
            .AddOptions<DeliveryOptions>()
            .BindConfiguration(DeliveryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeliveryOptions>, DeliveryOptionsValidator>();

        // The gauges' reader on the runtime key (§7.1), with a bounded connect timeout no query inherits, since a
        // callback that waits stalls every other one; through a factory, so the container disposes what it built.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Notifications"))
            {
                ConnectTimeout = NotificationStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<INotificationStats>(
            _ => new NotificationStats(new SqlConnectionFactory(metricsConnectionString)));

        // The send worker's counter and gauges, a singleton so one meter holds one set (§13.6).
        services.AddSingleton<NotificationMetrics>();

        // Both send loops' pace; an unset section keeps the constants it replaces, and only ADR-157's stress
        // override sets it. A lease a row could outlive refuses the host at start.
        services
            .AddOptions<SendPacingOptions>()
            .BindConfiguration(SendPacingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SendPacingOptions>, AnnotatedOptionsValidator<SendPacingOptions>>();

        // The send pass; the generic overload, so a suite can remove it by its ImplementationType (§12.4).
        services.AddScoped<SendClaims>();
        services.AddHostedService<SendWorker>();

        // The push pass, a loop of its own so a provider's wait never holds a mail notice, nor the relay's a push.
        services.AddScoped<PushClaims>();
        services.AddHostedService<PushSendWorker>();

        // ADR-053's three windows, applied by a pass of its own; by implementation type, which §12.4's fixture
        // removes it by, so its start-up pass never races a seed.
        services.AddHostedService<NotificationsRetentionService>();

        // The one retention service §9.5 asks for; this host stops its services concurrently (Program).
        services.AddHostedService<RetentionPurgeService>();

        // ADR-052's contact row, the one table here a mailbox lands in.
        services.AddScoped<IContactStore, SqlContactStore>();

        return services;
    }
}
