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

/// <summary>§9's bus registration, per-service because each service's consumers and endpoints are its own.</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's Consumes column; one queue, as both events dispatch a command with no failure branch.</summary>
    public const string EventsQueue = "payments-events";

    /// <summary>§3.2's Accepts column; Ordering's <c>Endpoints.PaymentsQueue</c> must name it too.</summary>
    public const string CommandsQueue = "payments-commands";

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
            x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();

            // §3.2's Accepts column.
            x.AddConsumer<CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>>();

            // ADR-021's scheduler; without UseDelayedMessageScheduler below, redelivery cannot schedule.
            x.AddDelayedMessageScheduler();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // ADR-021's transport half, the delayed-exchange plugin; on a broker without it redelivery hangs.
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
                            // No immediate retry: the first two are terminal, and a wait is the redelivery's above.
                            r.Ignore<ContractMappingException>();
                            r.Ignore<PaymentMismatchException>();
                            r.Ignore<PaymentOrderNotYetKnownException>();

                            RetryPolicy.Standard(r);
                        });

                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>>(context);
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
