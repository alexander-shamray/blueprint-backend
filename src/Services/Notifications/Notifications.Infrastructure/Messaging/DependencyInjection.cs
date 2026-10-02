using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.Infrastructure.Messaging;

/// <summary>§9's bus, per service because its consumers and receive endpoints are its own (§9.6).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager, so a host with no broker configured does not start; an empty environment variable counts as none.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        services.AddMassTransit(x =>
        {
            // On by default; §13.2 owns this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // No ConfigureEndpoints(context): it would give a consumer a queue with neither the inbox filter
                // nor the retry policy, and §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5). WaitUntilStarted
        // stays false, so a broker outage fails readiness rather than boot.
        return services;
    }
}
