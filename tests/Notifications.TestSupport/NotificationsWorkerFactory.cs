using System.Globalization;
using Common.Infrastructure.Messaging;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Mail;

namespace Notifications.TestSupport;

/// <summary>The real Notifications host over caller-supplied dependencies (§12.4).</summary>
public class NotificationsWorkerFactory(
    string connectionString,
    string rabbitConnectionString,
    string mailHost = NotificationsWorkerFactory.UnreachableRelay,
    int mailPort = NotificationsWorkerFactory.LocalRelayPort,
    string mailSecurity = "None",
    string? mailUserName = null,
    string? mailPassword = null,
    string mailFrom = NotificationsWorkerFactory.LocalFrom)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The relay a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableRelay = "relay.invalid";

    /// <summary>Mailpit's SMTP port, the one §14.1's unit reaches on the Compose network.</summary>
    public const int LocalRelayPort = Mailpit.SmtpPort;

    /// <summary>The sender a host names when a test gives none, on a domain RFC 2606 reserves.</summary>
    public const string LocalFrom = "Commerce <no-reply@commerce.test>";

    /// <summary>A relay credential that satisfies the rule and is unmistakably not one (docs/secrets.md).</summary>
    public const string NotARelayPassword = "not-a-real-relay-password";

    /// <summary>Supplies only §7.1's runtime connection; the host must not read <c>NotificationsMigrator</c>.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:Notifications", connectionString)
            .UseSetting("ConnectionStrings:RabbitMq", rabbitConnectionString)
            .UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority)
            .UseSetting(MailOptions.HostKey, mailHost)
            .UseSetting(MailOptions.PortKey, mailPort.ToString(CultureInfo.InvariantCulture))
            .UseSetting(MailOptions.FromKey, mailFrom)
            .UseSetting(MailOptions.SecurityKey, mailSecurity)
            .UseSetting(MailOptions.UserNameKey, mailUserName)
            .UseSetting(MailOptions.PasswordKey, mailPassword)
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
