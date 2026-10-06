using System.Globalization;
using Common.Infrastructure.Identity;
using Common.Infrastructure.Messaging;
using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Retention;
using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;

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
    string mailFrom = NotificationsWorkerFactory.LocalFrom,
    string contactSourceBaseUrl = NotificationsWorkerFactory.UnreachableContactSource,
    IReadOnlyList<string>? languages = null,
    string timeZone = NotificationsWorkerFactory.InventedTimeZone,
    string logRetention = NotificationsWorkerFactory.InventedLogRetention,
    string contactRetention = NotificationsWorkerFactory.InventedContactRetention,
    string orderRetention = NotificationsWorkerFactory.InventedOrderRetention,
    string giveUpAge = NotificationsWorkerFactory.InventedGiveUpAge)
    : WebApplicationFactory<Program>
{
    /// <summary>The authority every host must name (§11.3); <c>.invalid</c> never resolves.</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>ADR-053 rule 2's made-up language set, Kazakh first so a test reads the set's own order.</summary>
    public static readonly IReadOnlyList<string> InventedLanguages = ["kk", "en"];

    /// <summary>Thirteen and three-quarter hours ahead in October, so a date shows its side of midnight.</summary>
    public const string InventedTimeZone = "Pacific/Chatham";

    /// <summary>A window no deployment would choose, so a test passing under it read its configuration.</summary>
    public const string InventedLogRetention = "1013.00:00:00";

    /// <inheritdoc cref="InventedLogRetention"/>
    public const string InventedContactRetention = "17.00:00:00";

    /// <inheritdoc cref="InventedLogRetention"/>
    public const string InventedOrderRetention = "71.00:00:00";

    /// <summary>A give-up age no deployment would choose, inside <c>RetentionPolicy.InboxWindow</c>'s week.</summary>
    public const string InventedGiveUpAge = "2.07:00:00";

    /// <summary>The relay a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableRelay = "relay.invalid";

    /// <summary>Mailpit's SMTP port, the one §14.1's unit reaches on the Compose network.</summary>
    public const int LocalRelayPort = Mailpit.SmtpPort;

    /// <summary>The sender a host names when a test gives none, on a domain RFC 2606 reserves.</summary>
    public const string LocalFrom = "Commerce <no-reply@commerce.test>";

    /// <summary>A relay credential that satisfies the rule and is unmistakably not one (docs/secrets.md).</summary>
    public const string NotARelayPassword = "not-a-real-relay-password";

    /// <summary>The test contact source: HTTPS, which no environment refuses; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableContactSource = "https://keycloak.invalid/";

    /// <summary>The realm every contact is read from: §14.1's, and the realm export's.</summary>
    public const string LocalRealm = "commerce";

    /// <summary>The scope this host requests, whose mapper writes the claim its grant lives in (ADR-052).</summary>
    public const string ContactScope = "roles";

    /// <summary>The token source the credential handler draws on, so no test needs an identity provider.</summary>
    public RecordingTokenCache Tokens { get; } = new();

    /// <summary>The host's commit fault on a notice marked sent, disarmed until a test arms it.</summary>
    public SentCommitFaults CommitFaults { get; } = new();

    /// <summary>The host's log, captured beside the providers the host configures rather than replacing them.</summary>
    public CapturedLogs CapturedLogs { get; } = new();

    /// <summary>The settings a worker needs to start; the host must not read <c>NotificationsMigrator</c>.</summary>
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
            .UseSetting(ContactRegistration.BaseUrlKey, contactSourceBaseUrl)
            .UseSetting(ContactRegistration.RealmKey, LocalRealm)
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", "notifications-worker-test")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:Scope", ContactScope)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:TimeZone", timeZone)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:LogRetention", logRetention)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:ContactRetention", contactRetention)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:OrderRetention", orderRetention)
            .UseSetting($"{DeliveryOptions.SectionName}:GiveUpAge", giveUpAge)
            // A list binds by index, and an empty one sets no key at all, which is the refusal a test asks for.
            .ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    (languages ?? InventedLanguages)
                        .Select((language, index) =>
                            new KeyValuePair<string, string?>(
                                $"{NotificationsJurisdictionOptions.SectionName}:Languages:" +
                                index.ToString(CultureInfo.InvariantCulture),
                                language))))
            .ConfigureLogging(logging => logging.AddProvider(CapturedLogs))
            .ConfigureServices(services =>
            {
                ConfigureAuthentication(services);

                ConfigureTokens(services);

                // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.
                ServiceDescriptor purge = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(RetentionPurgeService));
                services.Remove(purge);

                services.AddSingleton<RetentionPurgeService>();

                // The send pass, by the same match, so its tick cannot send a row underneath an assertion.
                ServiceDescriptor send = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(SendWorker));
                services.Remove(send);

                services.AddSingleton<SendWorker>();

                // The statutory windows' pass, by the same match and for the same reason.
                ServiceDescriptor retention = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(NotificationsRetentionService));
                services.Remove(retention);

                services.AddSingleton<NotificationsRetentionService>();
            })
            .ConfigureTestServices(services =>
                services.ConfigureDbContext<NotificationsDbContext>(o => o.AddInterceptors(CommitFaults)));

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
