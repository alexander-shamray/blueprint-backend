using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application;
using Payments.Application.Intents;
using Payments.Application.Intents.AuthorisePayment;

namespace Payments.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, per-service rather than common because this is
/// where a service's consumers, sagas and receive endpoints are configured
/// (§9.6): <c>UsingRabbitMq</c>, the consumers and the receive endpoints are
/// each service's own.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// §3.2's Consumes column for Payments. One queue for both events: each
    /// dispatches a command with no failure branch, so they share one retry
    /// vocabulary.
    /// </summary>
    public const string EventsQueue = "payments-events";

    /// <summary>
    /// §3.2's Accepts column for Payments. The address Ordering's saga sends
    /// to is <c>Endpoints.PaymentsQueue</c> in Ordering.Infrastructure, and
    /// the two must name one queue.
    /// </summary>
    public const string CommandsQueue = "payments-commands";

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
            // MassTransit 8.5 reports anonymous usage data to a vendor
            // endpoint after the bus starts, enabled by default. §13.2 owns
            // this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column. Registering and binding are two statements and both
            // are needed; a consumer registered and never bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();

            // §3.2's Accepts column.
            x.AddConsumer<CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>>();

            // ADR-021's scheduler, the half that registers IMessageScheduler.
            // UseDelayedMessageScheduler below is the other half, and either
            // alone leaves the command endpoint's redelivery unable to schedule.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // The transport half of ADR-021's scheduler: RabbitMQ's delayed
                // exchange, a plugin the image deploy/compose builds carries.
                // On a broker without it the first redelivery hangs.
                cfg.UseDelayedMessageScheduler();

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        e.UseMessageRetry(r =>
                        {
                            // A provider's 409 on the void key is terminal: the same key
                            // and different figures is a defect no retry fixes (§9.8).
                            r.Ignore<PaymentMismatchException>();

                            RetryPolicy.Standard(r);
                        });

                        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
                        // the inbox row before the buffered sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                    });

                cfg.ReceiveEndpoint(
                    CommandsQueue,
                    e =>
                    {
                        // Outermost: a record that has not arrived is a wait (§3.2), so the
                        // message is released and delivered again later rather than held.
                        e.UseDelayedRedelivery(r =>
                        {
                            r.Handle<PaymentOrderNotYetKnownException>();
                            r.Intervals([.. RedeliveryLadder.Intervals]);
                        });

                        e.UseMessageRetry(r =>
                        {
                            // Terminal: a malformed contract, a mismatch and a wait each get
                            // nothing from an immediate retry — the first two never will, and
                            // the third is the redelivery's above.
                            r.Ignore<ContractMappingException>();
                            r.Ignore<PaymentMismatchException>();
                            r.Ignore<PaymentOrderNotYetKnownException>();

                            RetryPolicy.Standard(r);
                        });

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>>(context);
                    });

                // No ConfigureEndpoints, deliberately: for a registered
                // consumer with no explicit binding it manufactures a queue
                // named after the consumer type, with neither the inbox filter
                // nor the retry policy, and §9.8 admits no endpoint without
                // InboxFilter<>. A consumer added later needs a line here as
                // well as an AddConsumer, and nothing at startup complains if
                // it gets one and not the other, but a forgotten binding is
                // then a message nobody consumes rather than one consumed off
                // the record.
            });
        });

        // No readiness line here or in AddPaymentsInfrastructure, and that is
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
