using System.Net.Security;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Transport;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shipping.Infrastructure.Messaging;

/// <summary>§9's bus registration, per-service because each service's consumers and endpoints are its own.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's Consumes column; one queue, as each event writes one row and makes no call.</summary>
    public const string EventsQueue = "shipping-events";

    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager, so a host with no broker, or an empty variable, fails to start rather than at bus start.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        services.AddMassTransit(x =>
        {
            // On by default, it sends usage data to a vendor; §13.2 owns this platform's telemetry.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column. Registering and binding are two statements and both
            // are needed; a consumer registered and never bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                Uri broker = new(connectionString);
                cfg.Host(broker, host =>
                {
                    // MassTransit reads TLS from the port and trusts any chain, so the scheme asks (ADR-079).
                    if (TransportSecurity.IsTls(broker))
                        host.UseSsl(ssl => ssl.EnforcePolicyErrors(SslPolicyErrors.RemoteCertificateChainErrors));
                });

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // Bare, with no redelivery: neither consumer meets a wait or a §9.8 exclusion.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
                        // the inbox row before the buffered sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                    });

                // No ConfigureEndpoints: it would bind an unbound consumer to a queue with no InboxFilter<>,
                // which §9.8 admits on no endpoint, so a new consumer needs a binding here as well.
            });
        });

        // AddMassTransit registers the "masstransit-bus" ready check §13.5 picks up. WaitUntilStarted stays
        // false, so a broker outage leaves a pod unready rather than unable to boot (§13.5).
        return services;
    }
}
