using System.Net.Security;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Privacy.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Switching;
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

    /// <summary>§11.7's erasure request, on its own endpoint so no other consumer's failure holds it.</summary>
    public const string PrivacyQueue = "shipping-privacy";

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

        // ADR-113's switch, before the bus so the observer below can resolve it.
        services.AddOperatorSwitches(configuration);

        services.AddMassTransit(x =>
        {
            // On by default, it sends usage data to a vendor; §13.2 owns this platform's telemetry.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column. Registering and binding are two statements and both
            // are needed; a consumer registered and never bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();

            // §11.7's erasure request, which every holder of personal data consumes (ADR-092).
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>();
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                // First, since the switch sees only the endpoints configured after it (ADR-113). The topology bus
                // shares this one's broker, so a deferred endpoint's bindings are declared where it consumes (ADR-145).
                Uri broker = new(connectionString);
                cfg.ConnectOperatorSwitch(
                    context,
                    topology => Bus.Factory.CreateUsingRabbitMq(d =>
                    {
                        UseBroker(d, broker);
                        topology(d);
                    }));

                UseBroker(cfg, broker);

                cfg.SwitchedReceiveEndpoint(
                    context,
                    EventsQueue,
                    e =>
                    {
                        // Bare, with no redelivery: neither consumer meets a wait or a §9.8 exclusion.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // The inbox around the in-memory outbox, so its row commits after the flush (§9.8).
                        e.UseInboxAndInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                    });

                cfg.SwitchedReceiveEndpoint(
                    context,
                    PrivacyQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        e.UseInboxAndInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>(context);
                    });

                // No ConfigureEndpoints: it would bind an unbound consumer to a queue with no InboxFilter<>,
                // which §9.8 admits on no endpoint, so a new consumer needs a binding here as well.
            });
        });

        // AddMassTransit registers the "masstransit-bus" ready check §13.5 picks up. WaitUntilStarted stays
        // false, so a broker outage leaves a pod unready rather than unable to boot (§13.5).
        return services;
    }

    // Both buses' broker: the host's and the operator switch's topology bus, which must not drift apart.
    private static void UseBroker(IRabbitMqBusFactoryConfigurator cfg, Uri broker) =>
        cfg.Host(broker, host =>
        {
            // MassTransit reads TLS from the port and trusts any chain, so the scheme asks (ADR-079).
            if (TransportSecurity.IsTls(broker))
                host.UseSsl(ssl => ssl.EnforcePolicyErrors(SslPolicyErrors.RemoteCertificateChainErrors));
        });
}
