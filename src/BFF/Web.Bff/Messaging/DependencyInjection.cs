using System.Net.Security;
using Common.Contracts;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Privacy.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Transport;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;
using Web.Bff.Privacy;

namespace Web.Bff.Messaging;

/// <summary>The bus: the projection's queue and erasure's; it publishes nothing and sends one completion.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's BFF row: the projection's events, each of which writes rows here and calls nothing.</summary>
    public const string EventsQueue = "bff-order-events";

    /// <summary>§11.7's erasure request, on its own endpoint so no projection failure holds it.</summary>
    public const string PrivacyQueue = "bff-privacy";

    /// <summary>ADR-177's partitions, well past the endpoint's concurrency, so two orders seldom share one.</summary>
    public const int OrderPartitions = 256;

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

        // §9.5's filter names DbContext. An alias, never a second registration, beside the filter that needs it.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<BffDbContext>());

        // §11.7's report to Privacy, sent from the consumer's scope so the endpoint's outbox holds it (ADR-094).
        services.AddScoped<IErasureReporter, ErasureReporter>();

        services.AddMassTransit(x =>
        {
            // On by default; §13.2 owns this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // Registering and binding are two statements and both are needed.
            x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentAuthorised>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentRefunded>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDispatched>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDelivered>>();
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();

            // §11.7's erasure request, which every holder of personal data consumes (ADR-092).
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>();
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>();

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
                        // Bare, with no redelivery: no handler meets a wait, and none maps a command (§9.8).
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8).
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One order's events one at a time in this host, so a pair published together waits here and
                        // not on its row's lock (ADR-177). On the consumer's pipe, which the endpoint's retry wraps.
                        IPartitioner byOrder = e.CreatePartitioner(OrderPartitions);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(
                            context,
                            ByOrder<OrderPlaced>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(
                            context,
                            ByOrder<OrderConfirmed>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(
                            context,
                            ByOrder<OrderCancelled>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentAuthorised>>(
                            context,
                            ByOrder<PaymentAuthorised>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentRefunded>>(
                            context,
                            ByOrder<PaymentRefunded>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(
                            context,
                            ByOrder<ShipmentDispatched>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDelivered>>(
                            context,
                            ByOrder<ShipmentDelivered>(byOrder, m => m.OrderId));
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                    });

                cfg.ReceiveEndpoint(
                    PrivacyQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>(context);
                    });

                // No ConfigureEndpoints(context): §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5).
        return services;
    }

    /// <summary>An order event's consumer, each delivery waiting for its order's partition (ADR-177).</summary>
    /// <remarks>Only the configuration, so each binding stays a line the messaging gate reads (§3.2).</remarks>
    private static Action<IConsumerConfigurator<IntegrationEventConsumer<T>>> ByOrder<T>(
        IPartitioner byOrder,
        Func<T, Guid> orderId)
        where T : class, IIntegrationEvent =>
        c => c.Message<T>(m => m.UsePartitioner(byOrder, x => orderId(x.Message)));
}
