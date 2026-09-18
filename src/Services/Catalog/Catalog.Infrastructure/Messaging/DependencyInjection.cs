using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, per-service rather than common because this is
/// where a service's consumers, sagas and receive endpoints are configured
/// (§9.6): <c>UsingRabbitMq</c>, the consumers and the receive endpoints are
/// each service's own.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager: a host with no broker configured must not start. Read inside
        // UsingRabbitMq's callback, the missing key would surface at bus start
        // — after the host is up, past ValidateOnBuild, in a background
        // service's log. IsNullOrWhiteSpace, not a null check: an empty
        // environment variable configures an empty string, and letting it
        // through defers the failure to the same place.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        services.AddMassTransit(x =>
        {
            // MassTransit 8.5 reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            x.AddStockLevelConsumer();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ConfigureStockLevelEndpoint(context);

                // §9.8 configures retry per endpoint, so the policy lives with
                // each endpoint. No ConfigureEndpoints(context), deliberately:
                // for a registered consumer with no
                // explicit binding it manufactures a queue named after the
                // consumer type, with neither the inbox filter nor the retry
                // policy, and §9.8 admits no endpoint without InboxFilter<>. A
                // consumer added here needs an explicit ReceiveEndpoint with
                // its own policy, which is what this absence forces.
            });
        });

        // No readiness line here or in AddCatalogInfrastructure, and that is
        // a decision: AddMassTransit registers the bus health check itself —
        // "masstransit-bus", tagged ready — so §13.5's predicate picks it up
        // with nothing further. MassTransitHostOptions stays at its defaults
        // (WaitUntilStarted = false): the host starts while the bus connects
        // in the background, and readiness carries the wait — blocking
        // startup on the broker would turn a RabbitMQ outage into a pod that
        // cannot boot, §13.5's restart-storm argument one dependency over.
        return services;
    }
}
