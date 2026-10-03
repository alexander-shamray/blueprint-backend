using Notifications.Application;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;
using Notifications.TestSupport;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>§13.6's registration rules, over a <c>ServiceCollection</c>; there are no outbox gauges (§3.2).</summary>
public class MetricsRegistrationTests
{
    /// <summary>Types deliberately not forced, each with the reason its instrument can go unbuilt.</summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The collection, not a built provider, which cannot enumerate its registrations.
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(p => p.ParameterType)
        ];

        // Both directions. Unforced-and-unexplained is the drift this exists
        // for; forced-but-unregistered is a host that will not start.
        registered
            .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
            .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

        forced.ShouldBeSubsetOf(registered);
    }

    /// <summary>The gate-coverage half: the selector holds every metrics type this service registers.</summary>
    [Fact]
    public void The_metrics_selector_actually_selects_something()
    {
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        registered.ShouldContain(typeof(MessagingMetrics));
        registered.ShouldContain(typeof(RequestMetrics));
        registered.ShouldContain(typeof(MailMetrics));
        registered.ShouldContain(typeof(ContactMetrics));
        registered.ShouldContain(typeof(NotificationMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service()
    {
        // ImplementationType rather than a resolve, which would pass if another line had constructed the type.
        BuildServices()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(MetricsInitialiser));
    }

    /// <summary>The registration helpers the worker's <c>Program</c> calls, over unreachable configuration.</summary>
    private static ServiceCollection BuildServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Notifications"] =
                        "Server=sql.invalid;Database=Notifications;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@notifications-rabbit.invalid:5672",

                    // Read eagerly by AddMailChannel; StartTls and a credential, as the environment is not Development.
                    [MailOptions.HostKey] = "notifications-relay.invalid",
                    [MailOptions.PortKey] = "587",
                    [MailOptions.FromKey] = NotificationsWorkerFactory.LocalFrom,
                    [MailOptions.SecurityKey] = "StartTls",
                    [MailOptions.UserNameKey] = "notifications",
                    [MailOptions.PasswordKey] = NotificationsWorkerFactory.NotARelayPassword,

                    // Read eagerly by AddContactSource; HTTPS because the environment below is not Development.
                    [ContactRegistration.BaseUrlKey] = "https://notifications-keycloak.invalid/",
                    [ContactRegistration.RealmKey] = "commerce"
                })
            .Build();

        ServiceCollection services = new();
        services.AddNotificationsApplication();
        services.AddNotificationsInfrastructure(configuration);
        services.AddMailChannel(configuration, new TestEnvironment());
        services.AddContactSource(configuration, new TestEnvironment());

        return services;
    }
}
