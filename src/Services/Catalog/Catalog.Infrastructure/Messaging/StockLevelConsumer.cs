using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;

namespace Catalog.Infrastructure.Messaging;

/// <summary>
/// §3.2's Consumes column for Catalog, and exactly it. Its own file because
/// Catalog is the scaffold's template and a rendered service subscribes to
/// nothing: the two calls into this file are what the scaffold strips.
/// </summary>
public static class StockLevelConsumer
{
    // Public, as Ordering's queue constants are: §9.5's inbox keys each row on
    // the endpoint name, so the name is part of what an inbox row means and
    // not this assembly's private detail.
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
                // RetryPolicy.Standard with nothing excluded, as Ordering's
                // projection endpoint: IntegrationEventConsumer<T> throws when
                // the §6.2 scan registered no handler, which no backoff
                // repairs, and §9.4 wants a misconfigured endpoint loud rather
                // than quick.
                e.UseMessageRetry(r => RetryPolicy.Standard(r));

                // Inbox before the in-memory outbox (§9.8): filters added first
                // are outermost, and the outbox flushes after the inner
                // pipeline returns, so the inbox row is the last write.
                e.UseConsumeFilter(typeof(InboxFilter<>), context);
                e.UseInMemoryOutbox(context);

                e.ConfigureConsumer<IntegrationEventConsumer<StockLevelChanged>>(context);
            });
}
