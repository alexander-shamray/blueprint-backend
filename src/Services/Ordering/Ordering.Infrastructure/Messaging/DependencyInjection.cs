using System.Data;
using System.Net.Security;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Privacy.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Transport;
using Common.Infrastructure.Validation;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

    /// <summary>§11.7's erasure request, on its own endpoint so no other consumer's failure holds it.</summary>
    public const string PrivacyQueue = "ordering-privacy";

    /// <summary>
    /// Above the most a transition stages, three, so one pass delivers them all and marks the row delivered; at
    /// MassTransit's default of one, each message and the mark took a transaction of their own (ADR-174).
    /// </summary>
    public const int SagaOutboxDeliveryLimit = 10;

    /// <summary>The saga's row lock, <c>UPDLOCK, ROWLOCK, SERIALIZABLE</c> on its own statement (ADR-112).</summary>
    public static ILockStatementProvider SagaLockStatement { get; } =
        new SqlServerLockStatementProvider(serializable: true);

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

        // Validated at start, as every host's options are (§15.4); a deployed host sets none, so each wait keeps
        // §9.6's own delay.
        services
            .AddOptions<OrderFulfilmentTimeouts>()
            .Bind(configuration.GetSection(OrderFulfilmentTimeouts.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<OrderFulfilmentTimeouts>,
            AnnotatedOptionsValidator<OrderFulfilmentTimeouts>>();

        // The saga stamps each state it enters from this clock (§9.6); the container would otherwise pick the
        // parameterless constructor and drop the delays bound above.
        services.TryAddSingleton(TimeProvider.System);

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

            // §11.7's erasure request, which every holder of personal data consumes (ADR-092).
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>();
            x.AddConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>();

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

                    // The hint, not the transaction, takes the key-range lock two first deliveries need (ADR-112).
                    r.LockStatementProvider = SagaLockStatement;
                });

            // ADR-032's transactional outbox, for the saga's sends and schedules; UseBusOutbox() is not called,
            // because §9.4's application outbox owns the request path.
            x.AddEntityFrameworkOutbox<OrderingDbContext>(o =>
            {
                o.UseSqlServer();

                // The saga repository joins this transaction. Serializable here range-locked MassTransit's own
                // outbox reads and deadlocked the endpoint; RepeatableRead holds the same reads' row locks (ADR-112).
                o.IsolationLevel = IsolationLevel.ReadCommitted;

                // Its cleanup orders by Delivered alone, so it needs the index that put every delivery on one
                // page; ours, registered below, seeks the bucketed one instead (ADR-182).
                o.DisableInboxCleanupService();
            });

            // ADR-021's scheduler; without UseDelayedMessageScheduler below, the first Schedule(…) throws.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                Uri broker = new(connectionString);
                cfg.Host(broker, host =>
                {
                    // MassTransit reads TLS from the port and trusts any chain, so the scheme asks (ADR-079).
                    if (TransportSecurity.IsTls(broker))
                        host.UseSsl(ssl => ssl.EnforcePolicyErrors(SslPolicyErrors.RemoteCertificateChainErrors));
                });

                // ADR-021's transport half, the delayed-exchange plugin; on a broker without it scheduling hangs.
                cfg.UseDelayedMessageScheduler();

                cfg.ReceiveEndpoint(
                    CatalogEventsQueue,
                    e =>
                    {
                        // Nothing excluded: backoff cannot fix a missing handler, which still reaches the error queue.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // The inbox around the in-memory outbox, so its row commits after the flush (§9.8).
                        e.UseInboxAndInMemoryOutbox(context);

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

                        e.UseInboxAndInMemoryOutbox(context);

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

                        e.UseInboxAndInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<StockReserved>>(context);
                    });

                cfg.ReceiveEndpoint(
                    PrivacyQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        e.UseInboxAndInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteRequested>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PersonalDataDeleteClosed>>(context);
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
                        e.UseEntityFrameworkOutbox<OrderingDbContext>(
                            context,
                            o => o.MessageDeliveryLimit = SagaOutboxDeliveryLimit);

                        e.ConfigureSaga<OrderFulfilmentState>(context);
                    });

                // No ConfigureEndpoints: it would bind an unbound consumer to a queue with no InboxFilter<>,
                // which §9.8 admits on no endpoint, so a new consumer needs a binding here as well.
            });
        });

        // In place of the factory AddEntityFrameworkOutbox registers, which takes four or more a message (ADR-174).
        services.Replace(
            ServiceDescriptor.Scoped<IOutboxContextFactory<OrderingDbContext>, SagaOutboxContextFactory>());

        // In place of the cleanup AddEntityFrameworkOutbox would register (ADR-182).
        services.AddHostedService<SagaInboxCleanupService>();

        // AddMassTransit registers the "masstransit-bus" ready check §13.5 picks up. WaitUntilStarted stays
        // false, so a broker outage leaves a pod unready rather than unable to boot (§13.5).
        return services;
    }
}
