using Common.Infrastructure.Messaging;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Notifications.TestSupport;

/// <summary>The real Notifications host over caller-supplied dependencies (§12.4).</summary>
public class NotificationsWorkerFactory(string connectionString, string rabbitConnectionString)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>NotificationsMigrator</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Notifications", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();
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
