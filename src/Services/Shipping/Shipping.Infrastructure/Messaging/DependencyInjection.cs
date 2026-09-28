using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shipping.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, per-service rather than common because this is
/// where a service's consumers, sagas and receive endpoints are configured
/// (§9.6): <c>UsingRabbitMq</c>, the consumers and the receive endpoints are
/// each service's own.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// §3.2's Consumes column for Shipping. One queue for both events: each
    /// writes one row in this service's own database and makes no call, so
    /// neither can meet a fault that is a wait and one retry vocabulary
    /// covers both (spec, section 8).
    /// </summary>
    public const string EventsQueue = "shipping-events";

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

            // §3.2's Consumes column. Registering and binding are two
            // statements and both are needed; a consumer registered and never
            // bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // RetryPolicy.Standard bare, and no UseDelayedRedelivery:
                        // neither consumer can meet a fault that is a wait, and
                        // no mapping exception is possible because neither
                        // message is mapped by an ICommandMessageMapper —
                        // §9.8's exclusions have nothing to exclude here.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other
                        // nesting commits the inbox row before the buffered
                        // sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                    });

                // §9.8 configures retry per endpoint, so the policy lives with
                // each endpoint. No ConfigureEndpoints(context), deliberately:
                // for a registered consumer with no explicit binding it
                // manufactures a queue named after the consumer type, with
                // neither the inbox filter nor the retry policy, and §9.8
                // admits no endpoint without InboxFilter<>. A consumer added
                // here needs an explicit ReceiveEndpoint with its own policy,
                // which is what this absence forces.
            });
        });

        // No readiness line here or in AddShippingInfrastructure, and that is
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
