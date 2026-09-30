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

/// <summary>The real Payments host over caller-supplied dependencies (§12.4).</summary>
public class PaymentsApiFactory(
    string connectionString,
    string rabbitConnectionString,
    string providerBaseUrl = PaymentsApiFactory.UnreachableProvider,
    string? providerApiKey = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The provider a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableProvider = "http://psp.invalid/";

    /// <summary>§14.1's local-development placeholder for the provider key, which the simulator ignores.</summary>
    public const string LocalProviderApiKey = "local-dev-psp";

    /// <summary>The host's commit fault, disarmed until a test arms it.</summary>
    public CommitFaultInterceptor CommitFaults { get; } = new();

    /// <summary>The host's record of the order, observed.</summary>
    public ObservedOrderStore Orders { get; } = new();

    /// <summary>The host's provider seam, unarmed until a test arms it.</summary>
    public ProviderGateSeam ProviderGates { get; } = new();

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>PaymentsMigrator</c>.</summary>
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

                // Only the outbox dispatcher: MassTransit's bus is a hosted service too. Left running, the dispatcher
                // drains rows underneath assertions about them; AddHostedService<T> is what sets ImplementationType.
                ServiceDescriptor hosted = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(OutboxDispatcher));
                services.Remove(hosted);

                services.AddSingleton<OutboxDispatcher>();

                // §9.5's purge, removed by the same match, so a test that a row survives retention drives the pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

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
            {
                services.ConfigureDbContext<PaymentsDbContext>(o => o.AddInterceptors(CommitFaults));

                // Decorated rather than replaced, so every call still reaches the real statements and their locks.
                ServiceDescriptor store = services.Single(d => d.ServiceType == typeof(IPaymentOrderStore));
                services.Remove(store);
                services.AddScoped<IPaymentOrderStore>(sp => Orders.Wrap(
                    (IPaymentOrderStore)ActivatorUtilities.CreateInstance(sp, store.ImplementationType!)));

                // Decorated on the same terms, so the simulator's journal counts every charge and void.
                ServiceDescriptor provider = services.Single(d => d.ServiceType == typeof(IPaymentProvider));
                services.Remove(provider);
                services.AddSingleton(ProviderGates);
                services.AddTransient<IPaymentProvider>(sp => new PausingPaymentProvider(
                    (IPaymentProvider)provider.ImplementationFactory!(sp),
                    ProviderGates));
            });

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
