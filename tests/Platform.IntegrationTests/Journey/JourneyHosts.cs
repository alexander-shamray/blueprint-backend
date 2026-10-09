using Catalog.TestSupport;
using Common.Infrastructure.Outbox;
using Inventory.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Ordering.TestSupport;
using OrderingAuth = Ordering.TestSupport.TestAuthHandler;
using Payments.TestSupport;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;


namespace Platform.IntegrationTests.Journey;

/// <summary>Each service's factory with the background services its suite removes put back (§12.1).</summary>
/// <remarks>
/// A service suite drives one pass by hand; the journey asserts over no row a timer touches, so the timers run
/// and that they do is part of the proof (§12.4).
/// </remarks>
internal static class JourneyHosts
{
    /// <summary>Forwards a singleton the factory re-registered to the host's hosted services.</summary>
    internal static IServiceCollection RunsAsHostedService<T>(this IServiceCollection services)
        where T : class, IHostedService =>
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<T>());

    /// <summary>The shipped dispatcher's loop, behind the gate a scenario can hold.</summary>
    internal static IServiceCollection RunsOutboxBehind(this IServiceCollection services, OutboxGate gate) =>
        services.AddSingleton<IHostedService>(sp =>
            new GatedOutboxRunner(sp.GetRequiredService<OutboxDispatcher>(), gate));
}

internal sealed class JourneyCatalogFactory(
    string sql,
    string broker,
    string cache,
    string coordination,
    OutboxGate gate)
    : CatalogApiFactory(sql, broker, cache, coordination)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.RunsOutboxBehind(gate));
    }
}

internal sealed class JourneyOrderingFactory(
    string sql,
    string broker,
    string cache,
    string coordination,
    OutboxGate gate)
    : OrderingApiFactory(sql, broker, cache, coordination)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.RunsOutboxBehind(gate));
    }
}

internal sealed class JourneyInventoryFactory(
    string sql,
    string broker,
    string cache,
    string coordination,
    OutboxGate gate)
    : InventoryApiFactory(sql, broker, cache, coordination)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.RunsOutboxBehind(gate));
    }
}

internal sealed class JourneyPaymentsFactory(string sql, string broker, string provider, OutboxGate gate)
    : PaymentsApiFactory(sql, broker, provider)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.RunsOutboxBehind(gate));
    }
}

/// <summary>Shipping, whose address read meets Ordering's own service in process, not a stand-in for it.</summary>
internal sealed class JourneyShippingFactory(
    string sql,
    string broker,
    string carrier,
    TestServer ordering,
    OutboxGate gate,
    Jurisdiction jurisdiction)
    : ShippingWorkerFactory(
        sql,
        broker,
        carrier,
        addressRetention: jurisdiction.AddressRetention,
        trackingRetention: jurisdiction.TrackingRetention,
        giveUpAge: jurisdiction.ShippingGiveUp)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RunsOutboxBehind(gate);
            services.RunsAsHostedService<FulfilmentWorker>();
            services.RunsAsHostedService<TrackingWorker>();

            // Under the host's resilience pipeline and credential handler, so the call is the shipped one up to the
            // wire; the wire is Ordering's TestServer and the credential is the permission a test principal holds.
            services
                .AddHttpClient(AddressHop.ClientName)
                .ConfigurePrimaryHttpMessageHandler(ordering.CreateHandler)
                .AddHttpMessageHandler(() => new DeliveryAddressPrincipal());
        });
    }
}

/// <summary>Stands in for the access token's claims, which Ordering's test scheme reads from headers (§12.4).</summary>
internal sealed class DeliveryAddressPrincipal : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation(OrderingAuth.UserHeader, "shipping-worker");
        request.Headers.TryAddWithoutValidation(OrderingAuth.PermissionsHeader, "orders:delivery-address");

        return base.SendAsync(request, ct);
    }
}

internal sealed class JourneyNotificationsFactory(
    string sql,
    string broker,
    string relay,
    int relayPort,
    Uri contacts,
    Jurisdiction jurisdiction)
    : NotificationsWorkerFactory(
        sql,
        broker,
        mailHost: relay,
        mailPort: relayPort,
        contactSourceBaseUrl: contacts.ToString(),
        languages: jurisdiction.Languages,
        timeZone: jurisdiction.TimeZone,
        logRetention: jurisdiction.LogRetention,
        contactRetention: jurisdiction.ContactRetention,
        orderRetention: jurisdiction.OrderRetention,
        giveUpAge: jurisdiction.NotificationsGiveUp)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.RunsAsHostedService<SendWorker>());
    }
}
