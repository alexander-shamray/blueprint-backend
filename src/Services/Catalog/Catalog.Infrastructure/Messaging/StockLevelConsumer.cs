using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;

namespace Catalog.Infrastructure.Messaging;

/// <summary>§3.2's Consumes column for Catalog, and exactly it; Catalog-only, so the scaffold omits it.</summary>
public static class StockLevelConsumer
{
    // Public, because §9.5's inbox keys each row on the endpoint name.
    public const string Queue = "catalog-inventory-events";

    public static void AddStockLevelConsumer(this IBusRegistrationConfigurator x) =>
        x.AddConsumer<IntegrationEventConsumer<StockLevelChanged>>();

    public static void ConfigureStockLevelEndpoint(
        this IRabbitMqBusFactoryConfigurator cfg,
        IBusRegistrationContext context) =>
        cfg.ReceiveEndpoint(
            Queue,
            e =>
            {
                // Nothing excluded: no backoff repairs a missing handler, and §9.4 wants that loud, not quick.
                e.UseMessageRetry(r => RetryPolicy.Standard(r));

                // Inbox before the in-memory outbox (§9.8): the first filter is outermost, and the outbox flushes
                // after the inner pipeline returns, so the inbox row is the last write.
                e.UseConsumeFilter(typeof(InboxFilter<>), context);
                e.UseInMemoryOutbox(context);

                e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);
            });
}
