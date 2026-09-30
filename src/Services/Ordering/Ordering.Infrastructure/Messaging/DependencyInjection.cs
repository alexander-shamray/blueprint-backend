using System.Data;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Messaging;

/// <summary>§9's bus registration, per-service because each service's consumers and endpoints are its own.</summary>
public static class DependencyInjection
{
    /// <summary>§9.8's projection endpoint; §9.5's inbox keys each row on this name.</summary>
    public const string CatalogEventsQueue = "ordering-catalog-events";

    /// <summary>§3.2's Accepts column; <c>Endpoints.OrderingQueue</c> must name it too.</summary>
    public const string CommandsQueue = "ordering-commands";

    /// <summary>§9.8's saga endpoint, for §9.6's fulfilment events.</summary>
    public const string FulfilmentSagaQueue = "ordering-fulfilment-saga";

    /// <summary>Inventory's <c>StockReserved</c>, read here beside the saga, on its own retry policy (§9.6).</summary>
    public const string StockEventsQueue = "ordering-stock-events";

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
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();
            x.AddConsumer<IntegrationEventConsumer<PriceChanged>>();
            x.AddConsumer<IntegrationEventConsumer<ProductDiscontinued>>();

            // Consumed here and by the saga, because it means two things (StockEventsQueue).
            x.AddConsumer<IntegrationEventConsumer<StockReserved>>();

            // §3.2's Accepts column.
            x.AddConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>();
            x.AddConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>();
            x.AddConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>();
            x.AddConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>();

            // §9.6's state machine over the service's own database; the in-memory repository loses orders on restart.
            x
                .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<OrderingDbContext>();
                    // Pessimistic, so two events for one order serialise; optimistic retry would replay transitions.
                    r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                });

            // ADR-032's transactional outbox, for the saga's sends and schedules; UseBusOutbox() is not called,
            // because §9.4's application outbox owns the request path.
            x.AddEntityFrameworkOutbox<OrderingDbContext>(o =>
            {
                o.UseSqlServer();

                // Serializable, because the saga repository joins this filter's transaction and Pessimistic needs
                // the key-range lock only Serializable takes (ADR-032).
                o.IsolationLevel = IsolationLevel.Serializable;
            });

            // ADR-021's scheduler; without UseDelayedMessageScheduler below, the first Schedule(…) throws.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // ADR-021's transport half, the delayed-exchange plugin; on a broker without it scheduling hangs.
                cfg.UseDelayedMessageScheduler();

                cfg.ReceiveEndpoint(
                    CatalogEventsQueue,
                    e =>
                    {
                        // Nothing excluded: backoff cannot fix a missing handler, which still reaches the error queue.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
                        // the inbox row before the buffered sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PriceChanged>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductDiscontinued>>(context);
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

                        e.ConfigureConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>(context);
                    });

                // Off the saga endpoint, for the reason StockEventsQueue states.
                cfg.ReceiveEndpoint(
                    StockEventsQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<StockReserved>>(context);
                    });

                cfg.ReceiveEndpoint(
                    FulfilmentSagaQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // A duplicate OrderPlaced after finalisation would start a second workflow (§9.5).
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);

                        // The saga's sends commit in the instance's own transaction (ADR-032); both inboxes stay,
                        // as each makes a guarantee the other does not.
                        e.UseEntityFrameworkOutbox<OrderingDbContext>(context);

                        e.ConfigureSaga<OrderFulfilmentState>(context);
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
