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

/// <summary>
/// The bus registration of §9, per-service rather than common because this is
/// where a service's consumers, sagas and receive endpoints are configured
/// (§9.6): <c>UsingRabbitMq</c>, the consumers and the receive endpoints are
/// each service's own.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// §9.4's and §9.8's projection endpoint, by the name both chapters print.
    /// A constant because §9.5's inbox keys each row on the endpoint name, and
    /// the assertions that read those rows back have to agree with it.
    /// </summary>
    /// <remarks>
    /// Public, unlike §9.6's <c>Endpoints</c> class: that one holds the
    /// <c>queue:</c> addresses the saga sends to, and nothing outside this
    /// assembly sends; this is the name a queue is declared under, which is
    /// what the inbox row records and therefore what a test has to name.
    /// </remarks>
    public const string CatalogEventsQueue = "ordering-catalog-events";

    /// <summary>
    /// §9.4's command endpoint, for the commands §3.2 says Ordering accepts.
    /// The name must match <c>Endpoints.OrderingQueue</c>, or the saga sends
    /// into a void: a command addressed to an undeclared queue is not an error.
    /// </summary>
    public const string CommandsQueue = "ordering-commands";

    /// <summary>
    /// §9.8's saga endpoint, which receives the fulfilment events of §9.6.
    /// </summary>
    public const string FulfilmentSagaQueue = "ordering-fulfilment-saga";

    /// <summary>
    /// The endpoint for Inventory's <c>StockReserved</c>, which means two
    /// things to this service and only one of them is the saga's.
    /// </summary>
    /// <remarks>
    /// The saga reads it to decide what to ask for next; the order has to
    /// record that its stock is held, which is <c>Order.ConfirmStock</c> (§5.4).
    /// It does not share the saga endpoint because a consumer there would
    /// inherit a retry policy written for a state machine, whose failures are
    /// inapplicable transitions rather than the domain rejections
    /// <c>Order.ConfirmStock</c> produces: two failure vocabularies, two queues.
    /// </remarks>
    public const string StockEventsQueue = "ordering-stock-events";

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
            // MassTransit reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column. Registering a consumer and binding it are
            // two statements and both are needed: this makes the type
            // resolvable, and the ConfigureConsumer calls below put it on a
            // queue. A consumer registered and never bound receives nothing,
            // and looks exactly like one that does.
            x.AddConsumer<IntegrationEventConsumer<ProductPublished>>();
            x.AddConsumer<IntegrationEventConsumer<PriceChanged>>();
            x.AddConsumer<IntegrationEventConsumer<ProductDiscontinued>>();

            // Inventory's, consumed twice — here and through the saga's own
            // correlation — because it means two things; argued at
            // StockEventsQueue.
            x.AddConsumer<IntegrationEventConsumer<StockReserved>>();

            // §3.2's Accepts column, and exactly it. Each closed generic is a
            // separate registration because CommandConsumer<,> is common code
            // and the container builds the closed type (§9.4).
            x.AddConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>();
            x.AddConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>();
            x.AddConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>();
            x.AddConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>();

            // §9.6's state machine, over the service's own database. The
            // repository is not optional: MassTransit throws at startup
            // without one, and the in-memory repository §12.5 uses in tests
            // discards every in-flight order on restart.
            x
                .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<OrderingDbContext>();
                    // Pessimistic: two events for the same order can arrive
                    // concurrently — StockReserved and a timeout — and
                    // optimistic retry on a state machine replays transitions
                    // that already ran.
                    r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                });

            // ADR-032's transactional outbox, the second outbox table set §9.3
            // forbids, admitted for one endpoint because the prohibition's
            // argument does not reach it: the saga Sends and Schedules on the
            // bus rather than through the application outbox, and a scheduled
            // message cannot be staged in ordering.OutboxMessages because the
            // delay is a transport feature (ADR-021) no dispatcher of ours
            // could replay.
            //
            // This registers a scoped IOutboxContextFactory<OrderingDbContext>
            // and a hosted InboxCleanupService<OrderingDbContext>.
            // UseBusOutbox() is not called: it intercepts IPublishEndpoint and
            // ISendEndpointProvider outside a consume context, the API request
            // path §9.4's application outbox already owns.
            //
            // The cost is a second retention timer, over one table.
            // InboxCleanupService prunes ordering.InboxState only;
            // ordering.OutboxMessage is drained by the outbox middleware on
            // delivery; ordering.OutboxState is the bus-side outbox's table and
            // nothing here touches it, so it exists only for OutboxMessage's
            // foreign key and is permanently empty by design. InboxState is not
            // folded into RetentionPurgeService because deleting a row whose
            // messages have not been delivered is the message loss this outbox
            // exists to close.
            x.AddEntityFrameworkOutbox<OrderingDbContext>(o =>
            {
                o.UseSqlServer();

                // Serializable, because this filter opens the transaction and
                // the saga repository joins it, so the level in force is the
                // one set here — MassTransit's default is RepeatableRead, not
                // the Serializable the repository used on its own. Pessimistic
                // above needs a key-range lock: two deliveries both taking the
                // Initially branch for a CorrelationId with no row yet find
                // nothing, both insert, and one faults on the primary key, and
                // only Serializable takes that lock. Without this line the
                // option reads RepeatableRead.
                o.IsolationLevel = IsolationLevel.Serializable;
            });

            // ADR-021's scheduler, the half that registers IMessageScheduler;
            // UseDelayedMessageScheduler inside the transport callback is what
            // puts MessageSchedulerContext on the consume pipeline, where a
            // saga activity reaches for it. Either alone leaves Schedule(…)
            // throwing at the first OrderPlaced, since nothing resolves a
            // scheduler at startup.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // The transport half of ADR-021's scheduler. On RabbitMQ this
                // is the delayed message exchange, a plugin rather than a
                // broker feature — deploy/compose builds the image that
                // carries it. On a broker without it the bus starts clean and
                // the first schedule hangs, because MassTransit retries the
                // refused declare for ever.
                cfg.UseDelayedMessageScheduler();

                // §9.8's projection endpoint.
                cfg.ReceiveEndpoint(
                    CatalogEventsQueue,
                    e =>
                    {
                        // RetryPolicy.Standard with nothing excluded, unlike
                        // ordering-commands. IntegrationEventConsumer<T> throws
                        // when the §6.2 scan registered no handler, which no
                        // backoff repairs; it still reaches the error queue, a
                        // retry ladder later than it might, and §9.4 wants a
                        // misconfigured endpoint loud rather than quick.
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox before the in-memory outbox, a correctness rule
                        // (§9.8): filters added first are outermost, and the
                        // outbox flushes its buffered sends after the inner
                        // pipeline returns. The other order commits the inbox
                        // row first, so a failed flush leaves a message
                        // acknowledged, its sends lost, and the redelivery
                        // suppressed by the filter's own row. The context
                        // argument is required: the parameterless overload is
                        // obsolete at the pinned version, which ADR-019 makes
                        // an error.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One line per event in §3.2's Consumes column that
                        // Catalog owns. A handler with no line here is never
                        // invoked and looks correct while doing nothing, which
                        // is why the set is asserted rather than read off this
                        // file.
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductPublished>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PriceChanged>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ProductDiscontinued>>(context);
                    });

                // §9.4's command endpoint, and the one whose retry policy is
                // not RetryPolicy.Standard alone.
                cfg.ReceiveEndpoint(
                    CommandsQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                        {
                            // A malformed contract does not parse itself on a
                            // later attempt; retrying it burns the backoff and
                            // delays every message behind it before reaching
                            // the same error queue. Domain rejections are not
                            // on this list because they never throw —
                            // CommandConsumer acks, counts and logs them (§9.8).
                            r.Ignore<ContractMappingException>();

                            RetryPolicy.Standard(r);
                        });

                        // Inbox outside the in-memory outbox, for the reason
                        // the projection endpoint states above: the other
                        // nesting commits the inbox row before the buffered
                        // sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One per command in §3.2's Accepts column; a type
                        // missing here is sent into a queue that ignores it.
                        e.ConfigureConsumer<CommandConsumer<CancelOrder, CancelOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<ConfirmOrder, ConfirmOrderCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>>(context);
                    });

                // Ordering's own reaction to Inventory's reservation, off the
                // saga endpoint for the reason StockEventsQueue states. Same
                // policy as the projection endpoint — a consumer like any other.
                cfg.ReceiveEndpoint(
                    StockEventsQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<StockReserved>>(context);
                    });

                // §9.8's saga endpoint.
                cfg.ReceiveEndpoint(
                    FulfilmentSagaQueue,
                    e =>
                    {
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // The inbox, although a saga is idempotent by
                        // construction — that holds for non-initial events
                        // only. OrderPlaced is handled in Initially and
                        // SetCompletedWhenFinalized deletes the row, so a
                        // duplicate arriving after the workflow finished would
                        // start a new instance and reserve stock and authorise
                        // payment a second time (§9.4 is at-least-once). It
                        // costs nothing on a mid-transition crash: InboxFilter
                        // writes its row after the inner pipe returns (§9.5),
                        // so the redelivery does the work again.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);

                        // The one endpoint whose outbox is not the in-memory
                        // one (ADR-032). Other consumers publish through
                        // §9.4's application outbox, whose row commits with
                        // the aggregate, so an in-memory buffer there defers
                        // sends that are already durable. The saga Sends and
                        // Schedules on the bus directly, and an in-memory
                        // buffer flushes after the repository commits — a
                        // crash in that window left an order with nothing sent
                        // and no timeout to rescue it. This filter stages those
                        // sends in the instance's own transaction instead.
                        //
                        // Both inboxes stay. InboxFilter<> is §9.5's
                        // long-window duplicate suppressor, pruned on §9.4's
                        // retention, which stops a redelivered OrderPlaced
                        // starting a second workflow hours after the first
                        // finalised; MassTransit's InboxState is a short-window
                        // delivery record that tells this filter which
                        // committed messages it has already sent. Retiring
                        // either costs a guarantee the other never made.
                        e.UseEntityFrameworkOutbox<OrderingDbContext>(context);

                        e.ConfigureSaga<OrderFulfilmentState>(context);
                    });

                // No ConfigureEndpoints, deliberately. For a registered
                // consumer with no explicit binding it manufactures a queue
                // named after the consumer type, with neither the inbox filter
                // nor the retry policy, because both are per-endpoint
                // configuration an invented endpoint never receives — and §9.8
                // admits no endpoint without InboxFilter<>. The cost: a
                // consumer added later needs a line here as well as an
                // AddConsumer, and nothing at startup complains if it gets one
                // and not the other, but a forgotten binding is then a message
                // nobody consumes rather than one consumed off the record.
            });
        });

        // No readiness line here or in AddOrderingInfrastructure, and that is
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
