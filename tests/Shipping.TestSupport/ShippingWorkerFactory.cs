using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Persistence;
using Shipping.Infrastructure.Retention;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Identity;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AddressRegistration = Shipping.Infrastructure.Addresses.DependencyInjection;
using CarrierRegistration = Shipping.Infrastructure.Carrier.DependencyInjection;

namespace Shipping.TestSupport;

/// <summary>The real Shipping host over caller-supplied dependencies (§12.4).</summary>
public class ShippingWorkerFactory(
    string connectionString,
    string rabbitConnectionString,
    string carrierBaseUrl = ShippingWorkerFactory.UnreachableCarrier,
    string? carrierApiKey = null,
    string addressSourceBaseUrl = ShippingWorkerFactory.UnreachableAddressSource,
    string addressRetention = ShippingWorkerFactory.InventedAddressRetention,
    string trackingRetention = ShippingWorkerFactory.InventedTrackingRetention,
    string giveUpAge = ShippingWorkerFactory.InventedGiveUpAge)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The carrier a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableCarrier = "http://carrier.invalid/";

    /// <summary>§14.1's local-development placeholder for the carrier key, which the simulator ignores.</summary>
    public const string LocalCarrierApiKey = "local-dev-carrier";

    /// <summary>The address source a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAddressSource = "http://ordering-api.invalid/";

    /// <summary>ADR-053 rule 2's made-up jurisdiction, in a value no real one uses.</summary>
    /// <remarks>So a test passing under it read its configuration rather than a constant (ADR-053).</remarks>
    public const string InventedAddressRetention = "11.00:00:00";

    /// <inheritdoc cref="InventedAddressRetention"/>
    public const string InventedTrackingRetention = "23.00:00:00";

    /// <summary>A give-up age no deployment would choose, as <see cref="InventedAddressRetention"/> is.</summary>
    public const string InventedGiveUpAge = "5.07:00:00";

    /// <summary>The token source the credential handler draws on, so no test needs an identity provider.</summary>
    public RecordingTokenCache Tokens { get; } = new();

    /// <summary>The host's commit fault, disarmed until a test arms it.</summary>
    public ShipmentCommitFaults CommitFaults { get; } = new();

    /// <summary>The host's log, captured beside the providers the host configures rather than replacing them.</summary>
    public CapturedLogs CapturedLogs { get; } = new();

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>ShippingMigrator</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Shipping", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .UseSetting(CarrierRegistration.BaseUrlKey, carrierBaseUrl)
            .UseSetting(CarrierRegistration.ApiKeyKey, carrierApiKey ?? LocalCarrierApiKey)
            .UseSetting(AddressRegistration.BaseUrlKey, addressSourceBaseUrl)
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", "shipping-worker-test")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:Scope", "commerce-api")
            .UseSetting($"{ShippingJurisdictionOptions.SectionName}:AddressRetention", addressRetention)
            .UseSetting($"{ShippingJurisdictionOptions.SectionName}:TrackingRetention", trackingRetention)
            .UseSetting($"{FulfilmentOptions.SectionName}:GiveUpAge", giveUpAge)
            .ConfigureLogging(logging => logging.AddProvider(CapturedLogs))
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                ConfigureTokens(services);

                // Only the outbox dispatcher: MassTransit's bus is a hosted service too. Left running, the dispatcher
                // drains rows underneath assertions about them; AddHostedService<T> is what sets ImplementationType.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

                // The fulfilment worker, by the same match, so its tick cannot book a row underneath an assertion.
                ServiceDescriptor fulfilment = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(FulfilmentWorker));
                services.Remove(fulfilment);

                services.AddSingleton<FulfilmentWorker>();

                // The tracking worker, by the same match, so its tick cannot poll a row underneath an assertion.
                ServiceDescriptor tracking = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(TrackingWorker));
                services.Remove(tracking);

                services.AddSingleton<TrackingWorker>();

                // §9.5's purge, removed by the same match, so a test that a row survives retention drives the pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // The statutory windows' pass, by the same match and for the same reason.
                ServiceDescriptor retention = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(ShippingRetentionService));
                services.Remove(retention);

                services.AddSingleton<ShippingRetentionService>();

                // §9.4: added to rather than replaced, so a test cannot stage a type the real host would refuse.
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeSource()
                    .Add(typeof(AlwaysThrows).Assembly);

                // The projection handlers those events need; each layer scans itself (§6.2).
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            })
            .ConfigureTestServices(services =>
                services.ConfigureDbContext<ShippingDbContext>(o => o.AddInterceptors(CommitFaults)));

    /// <summary>Swaps the JWT scheme for <see cref="TestAuthHandler"/> (§12.4); a host may override it.</summary>
    protected virtual void ConfigureAuthentication(IServiceCollection services)
    {
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
        });

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
    }

    /// <summary>Puts <see cref="Tokens"/> in place of the host's own token source; a host may override it.</summary>
    protected virtual void ConfigureTokens(IServiceCollection services)
    {
        services.RemoveAll<ITokenCache>();
        services.AddSingleton<ITokenCache>(Tokens);
    }
}

file static class ServiceDescriptorExtensions
{
    /// <summary>The registered instance as itself, with a message a failed cast would not give.</summary>
    public static MessageTypeSource ShouldBeSource(this object? instance) =>
        instance as MessageTypeSource ??
            throw new InvalidOperationException(
                "MessageTypeSource is no longer registered as a singleton instance, so the test " +
                "assembly's events cannot be added to it before the map is built (§9.4).");
}
