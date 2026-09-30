using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure.Messaging;

/// <summary>§9's bus registration, per-service because each service's consumers and endpoints are its own.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's Accepts column; Ordering's <c>Endpoints.InventoryQueue</c> must name it too.</summary>
    public const string CommandsQueue = "inventory-commands";

    /// <summary>§3.2's Consumes column; one queue, as each dispatches a command acked on rejection (§9.8).</summary>
    public const string EventsQueue = "inventory-events";

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

            // §3.2's Accepts column, one closed CommandConsumer<,> per command (§9.4).
            x.AddConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>();
            x.AddConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>();

            // §3.2's Consumes column.
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDispatched>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // No exclusion: no mapper runs on this queue, so no ContractMappingException arises.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
                        // the inbox row before the buffered sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(context);
                    });

                cfg.ReceiveEndpoint(
                    CommandsQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                        {
                            // A malformed contract never parses on retry; domain rejections never throw (§9.8).
                            r.Ignore<ContractMappingException>();

                            RetryPolicy.Standard(r);
                        });

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>(context);
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
