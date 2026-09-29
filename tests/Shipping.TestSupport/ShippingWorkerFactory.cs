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

/// <summary>
/// The real Shipping host over caller-supplied dependencies (§12.4). One type
/// for both suites here — the host smoke points it at names that cannot
/// resolve, the container suite at running containers — so what differs
/// between them is the infrastructure and not the wiring.
/// </summary>
public class ShippingWorkerFactory(
    string connectionString,
    string rabbitConnectionString,
    string carrierBaseUrl = ShippingWorkerFactory.UnreachableCarrier,
    string? carrierApiKey = null,
    string addressSourceBaseUrl = ShippingWorkerFactory.UnreachableAddressSource,
    string addressRetention = ShippingWorkerFactory.InventedAddressRetention,
    string trackingRetention = ShippingWorkerFactory.InventedTrackingRetention)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// The authority every host over this <c>Program</c> must name (§11.3).
    /// Deliberately fake and deliberately unreachable — <c>.invalid</c> is
    /// reserved and never resolves, so a test that accidentally dials the
    /// authority fails loudly rather than reaching a real identity provider.
    /// Required rather than optional for the same reason both connection
    /// strings are: <c>AddJwtAuthentication</c> reads this key eagerly and
    /// throws naming it, so a host that cannot name its identity provider
    /// does not start.
    /// </summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>
    /// The carrier every host over this <c>Program</c> must name (§3.2).
    /// Unreachable because <c>.invalid</c> never resolves, so a test that dials
    /// the carrier by accident fails loudly rather than booking anything, and
    /// plain HTTP because the factory runs the host as Development, the one
    /// environment that allows it.
    /// </summary>
    public const string UnreachableCarrier = "http://carrier.invalid/";

    /// <summary>
    /// §14.1's local-development placeholder for the carrier key, which the
    /// simulator ignores. Required by the host (§15.4), so a caller that names
    /// none still gets one.
    /// </summary>
    public const string LocalCarrierApiKey = "local-dev-carrier";

    /// <summary>
    /// Where the address client points when a test does not care. Unreachable
    /// for the authority's reason: <c>.invalid</c> never resolves, so a test
    /// that dials Ordering by accident fails loudly.
    /// </summary>
    public const string UnreachableAddressSource = "http://ordering-api.invalid/";

    /// <summary>
    /// ADR-053 rule 2's made-up jurisdiction, and deliberately a value no real
    /// one uses: eleven days and twenty-three days match neither the six years
    /// nor the five that record's table names, so a test passing under them is
    /// a test that read its configuration rather than a constant.
    /// </summary>
    public const string InventedAddressRetention = "11.00:00:00";

    /// <inheritdoc cref="InventedAddressRetention"/>
    public const string InventedTrackingRetention = "23.00:00:00";

    /// <summary>
    /// The token source the credential handler draws on, replacing
    /// <c>CachingTokenClient</c> and its grant check so that no test needs an
    /// identity provider to prove what the handler does with a token.
    /// </summary>
    public RecordingTokenCache Tokens { get; } = new();

    /// <summary>
    /// The host's commit fault, disarmed until a test arms it. Installed on
    /// every host over this factory, because a disarmed interceptor changes
    /// nothing and one host per seam would be a container set per seam.
    /// </summary>
    public ShipmentCommitFaults CommitFaults { get; } = new();

    /// <summary>
    /// The host's log, captured. Added to the providers the host configures
    /// rather than replacing them, so what a test reads is what a deployment
    /// would write (spec, section 11).
    /// </summary>
    public CapturedLogs CapturedLogs { get; } = new();

    /// <summary>
    /// The RUNTIME connection of §7.1, and only that one. The host has no
    /// business reading <c>ShippingMigrator</c>, and a fixture that supplied
    /// both would hide it if it started. The bus key is required because
    /// <c>AddMassTransitMessaging</c> throws without it — every host over
    /// this Program needs one, reachable or not.
    /// </summary>
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
            .ConfigureLogging(logging => logging.AddProvider(CapturedLogs))
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                ConfigureTokens(services);

                // Remove only the outbox dispatcher, not every hosted
                // service: MassTransit registers its bus as one, and
                // RemoveAll<IHostedService>() would stop the broker and
                // silently disable every consumption test. Left running it
                // polls every 500 ms and drains rows underneath assertions
                // about them — tests that want it call
                // fixture.ProcessOutboxBatchAsync() explicitly.
                // AddShippingInfrastructure uses AddHostedService<T> rather
                // than a factory overload for exactly this match: a factory
                // registration leaves ImplementationType null.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

                // The fulfilment worker, by the same match and for the same
                // reason: its tick would book a row underneath an assertion
                // about it, so a test drives RunOnceAsync instead.
                ServiceDescriptor fulfilment = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(FulfilmentWorker));
                services.Remove(fulfilment);

                services.AddSingleton<FulfilmentWorker>();

                // The tracking worker, by the same match and for the same
                // reason: its tick would poll a row underneath an assertion
                // about it, so a test drives ProcessBatchAsync instead.
                ServiceDescriptor tracking = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(TrackingWorker));
                services.Remove(tracking);

                services.AddSingleton<TrackingWorker>();

                // §9.5's purge, removed and re-registered for the same two
                // reasons and by the same match. Its timer is an hour rather
                // than 500 ms, so it would not race an assertion in a run this
                // short — but a test asserting that an abandoned row survives
                // retention cannot be sure of that from a service it does not
                // drive, and "the pass never happened" and "the pass spared the
                // row" are the same green.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // §9.4. Adding, not replacing: the production assemblies stay,
                // so a test cannot stage a type the real host would refuse.
                // Without this, NameOf throws on the first builder call and
                // every outbox test fails before its assertion.
                //
                // Mutating the registered instance rather than re-registering
                // one, because MessageTypeSource is deliberately mutable for
                // exactly this and the map is built from it at first resolve.
                services
                    .Single(d => d.ServiceType == typeof(MessageTypeSource))
                    .ImplementationInstance
                    .ShouldBeSource()
                    .Add(typeof(AlwaysThrows).Assembly);

                // The projection handlers for two of those three events. Each
                // layer scans itself (§6.2), and this assembly is a layer the
                // production registration has no reason to know about.
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            })
            .ConfigureTestServices(services =>
                services.ConfigureDbContext<ShippingDbContext>(o => o.AddInterceptors(CommitFaults)));

    /// <summary>
    /// Replaces the JWT scheme with <see cref="TestAuthHandler"/> (§12.4)
    /// rather than configuring it: the endpoints under test sit behind
    /// <c>RequireAuthorization</c> (§11.4), so the alternative is a 401 on
    /// every call or a fixture fetching OIDC metadata over the network.
    /// </summary>
    /// <remarks>Virtual, because a host keeping the production scheme is the
    /// only thing that can prove <see cref="TestAuthHandler"/>'s headers mean
    /// nothing to a real deployment. Forbid is left unset and falls back to
    /// the challenge scheme, so the 403 is a bare status code.</remarks>
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

    /// <summary>Puts <see cref="Tokens"/> in place of the host's own token source.</summary>
    /// <remarks>Virtual, because only a host that keeps <c>Program</c>'s
    /// registration can prove which token source a deployment gets.</remarks>
    protected virtual void ConfigureTokens(IServiceCollection services)
    {
        services.RemoveAll<ITokenCache>();
        services.AddSingleton<ITokenCache>(Tokens);
    }
}

file static class ServiceDescriptorExtensions
{
    /// <summary>
    /// Reads the registered instance back as itself, with a message that says
    /// what changed if it ever stops being registered that way — a cast
    /// failing here would otherwise read as a null reference from a line that
    /// mentions no null.
    /// </summary>
    public static MessageTypeSource ShouldBeSource(this object? instance) =>
        instance as MessageTypeSource ??
            throw new InvalidOperationException(
                "MessageTypeSource is no longer registered as a singleton instance, so the test " +
                "assembly's events cannot be added to it before the map is built (§9.4).");
}
