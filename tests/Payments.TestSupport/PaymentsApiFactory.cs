using Payments.TestSupport.Outbox;
using Common.Application;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Outbox;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payments.Application.Orders;
using Payments.Application.Provider;
using Payments.Infrastructure.Persistence;
using ProviderRegistration = Payments.Infrastructure.Provider.DependencyInjection;

namespace Payments.TestSupport;

/// <summary>
/// The real Payments host over caller-supplied dependencies (§12.4). One type
/// for both suites here — the host smoke points it at names that cannot
/// resolve, the container suite at running containers — so what differs
/// between them is the infrastructure and not the wiring.
/// </summary>
public class PaymentsApiFactory(
    string connectionString,
    string rabbitConnectionString,
    string providerBaseUrl = PaymentsApiFactory.UnreachableProvider,
    string? providerApiKey = null)
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
    /// The provider every host over this <c>Program</c> must name (§3.2).
    /// Unreachable for the authority's reason — <c>.invalid</c> never
    /// resolves, so a test that dials the provider by accident fails loudly
    /// rather than authorising anything — and plain HTTP because the factory
    /// runs the host as Development, the one environment that allows it.
    /// Defaulted rather than required: a host that never calls the provider
    /// has nothing to say about where it is.
    /// </summary>
    public const string UnreachableProvider = "http://psp.invalid/";

    /// <summary>
    /// §14.1's local-development placeholder for the provider key, which the
    /// simulator ignores. Required by the host (§15.4), so a caller that
    /// names none still gets one.
    /// </summary>
    public const string LocalProviderApiKey = "local-dev-psp";

    /// <summary>
    /// The host's commit fault, disarmed until a test arms it. Installed on
    /// every host over this factory, because a disarmed interceptor changes
    /// nothing and one host per seam would be a container set per seam.
    /// </summary>
    public CommitFaultInterceptor CommitFaults { get; } = new();

    /// <summary>The host's record of the order, observed (spec, section 6).</summary>
    public ObservedOrderStore Orders { get; } = new();

    /// <summary>
    /// The host's provider seam, unarmed until a test arms it. Installed on
    /// every host over this factory, because an unarmed seam changes nothing
    /// and one host per seam would be a container set per seam.
    /// </summary>
    public ProviderGateSeam ProviderGates { get; } = new();

    /// <summary>
    /// The RUNTIME connection of §7.1, and only that one. The host has no
    /// business reading <c>PaymentsMigrator</c>, and a fixture that supplied
    /// both would hide it if it started. The bus key is required because
    /// <c>AddMassTransitMessaging</c> throws without it — every host over
    /// this Program needs one, reachable or not.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Payments", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .UseSetting(ProviderRegistration.BaseUrlKey, providerBaseUrl)
            .UseSetting(ProviderRegistration.ApiKeyKey, providerApiKey ?? LocalProviderApiKey)
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                // Remove only the outbox dispatcher, not every hosted
                // service: MassTransit registers its bus as one, and
                // RemoveAll<IHostedService>() would stop the broker and
                // silently disable every consumption test. Left running it
                // polls every 500 ms and drains rows underneath assertions
                // about them — tests that want it call
                // fixture.ProcessOutboxBatchAsync() explicitly.
                // AddPaymentsInfrastructure uses AddHostedService<T> rather
                // than a factory overload for exactly this match: a factory
                // registration leaves ImplementationType null.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                // Still resolvable directly, so tests can drive one pass.
                services.AddSingleton<OutboxDispatcher>();

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

                // The projection handlers those events need, short of the one
                // built to have none. Each layer scans itself (§6.2), and this
                // assembly is a layer the production registration has no
                // reason to know about.
                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);
            })
            .ConfigureTestServices(services =>
            {
                services.ConfigureDbContext<PaymentsDbContext>(o => o.AddInterceptors(CommitFaults));

                // Decorated rather than replaced, so every call still reaches
                // the real statements and their locks. Built from the
                // registered descriptor, because the store is internal to
                // Infrastructure and this assembly cannot name it.
                ServiceDescriptor store = services.Single(d => d.ServiceType == typeof(IPaymentOrderStore));
                services.Remove(store);
                services.AddScoped<IPaymentOrderStore>(sp => Orders.Wrap(
                    (IPaymentOrderStore)ActivatorUtilities.CreateInstance(sp, store.ImplementationType!)));

                // The provider, decorated on the same terms: the real adapter
                // still makes every call, so the simulator's journal is what a
                // test counts charges and voids by. Built from the registered
                // descriptor, because HttpPaymentProvider is internal to
                // Infrastructure. Transient, matching what it replaces: a
                // typed client's lifetime is the handler's, which is why the
                // arm lives on ProviderGates instead.
                ServiceDescriptor provider = services.Single(d => d.ServiceType == typeof(IPaymentProvider));
                services.Remove(provider);
                services.AddSingleton(ProviderGates);
                services.AddTransient<IPaymentProvider>(sp => new PausingPaymentProvider(
                    (IPaymentProvider)provider.ImplementationFactory!(sp),
                    ProviderGates));
            });

    /// <summary>
    /// Replaces the JWT scheme with <see cref="TestAuthHandler"/> (§12.4),
    /// rather than configuring it: the endpoints under test are behind
    /// <c>RequireAuthorization</c> (§11.4), and the alternative is either a
    /// 401 on every call or fetching OIDC metadata from an unreachable
    /// authority. Virtual, so the one host that keeps the production
    /// scheme can prove this handler's headers mean nothing to a real
    /// deployment. <c>DefaultForbidScheme</c> stays unset, so a 403 falls
    /// back to this handler's own inherited forbid.
    /// </summary>
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
