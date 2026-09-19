using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure.Messaging;

/// <summary>
/// The bus registration of §9, per-service rather than common because this is
/// where a service's consumers, sagas and receive endpoints are configured
/// (§9.6): <c>UsingRabbitMq</c>, the consumers and the receive endpoints are
/// each service's own.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// §9.4's command endpoint, for the commands §3.2's Accepts column gives
    /// this service. One queue for them all, unlike a queue per command:
    /// the saga addresses Inventory through exactly this endpoint for both
    /// a reserve and a release, and splitting them would buy no isolation —
    /// each is excluded from retry by the same
    /// <see cref="ContractMappingException"/> and runs under the same ladder
    /// below.
    /// </summary>
    public const string CommandsQueue = "inventory-commands";

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

            // §3.2's Accepts column, and exactly it. Each closed generic is a
            // separate registration because CommandConsumer<,> is common code
            // and the container builds the closed type (§9.4).
            x.AddConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>();
            x.AddConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                // §9.4's command endpoint, for the commands §3.2 says this
                // service accepts.
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

                        // Inbox before the in-memory outbox, a correctness
                        // rule (§9.8): filters added first are outermost, and
                        // the outbox flushes its buffered sends after the
                        // inner pipeline returns. The other order commits the
                        // inbox row first, so a failed flush leaves a message
                        // acknowledged, its sends lost, and the redelivery
                        // suppressed by the filter's own row.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        // One per command in §3.2's Accepts column; a type
                        // missing here is sent into a queue that ignores it.
                        e.ConfigureConsumer<CommandConsumer<ReserveStock, ReserveStockCommand>>(context);
                        e.ConfigureConsumer<CommandConsumer<ReleaseStock, ReleaseStockCommand>>(context);
                    });

                // No ConfigureEndpoints, deliberately. For a registered
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

        // No readiness line here or in AddInventoryInfrastructure, and that is
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
