using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff.Messaging;

/// <summary>ADR-051's bus: one receive endpoint for the projection's events, and nothing published.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's BFF row; one queue, as every event writes rows here and calls nothing.</summary>
    public const string EventsQueue = "bff-order-events";

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

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // Bare, with no redelivery: no handler meets a wait, and none maps a command (§9.8).
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8).
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentAuthorised>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentRefunded>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDelivered>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                    });

                // No ConfigureEndpoints(context): §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5).
        return services;
    }
}
