using System.Net.Security;
using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Transport;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Privacy.Application.ErasureRequests.RecordCompletion;

namespace Privacy.Infrastructure.Messaging;

/// <summary>§9's bus, per service because its consumers and receive endpoints are its own (§9.6).</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Where every holder sends its completion (ADR-094), spelt by each of them; renaming it silences the choreography.
    /// </summary>
    public const string CompletionsQueue = "privacy-completions";

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

        services.AddMassTransit(x =>
        {
            // On by default; §13.2 owns this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // §3.2's Accepts column: a completion is a command to this service, sent to its queue (ADR-094).
            x.AddConsumer<CommandConsumer<PersonalDataDeleteCompleted, RecordErasureCompletionCommand>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                Uri broker = new(connectionString);
                cfg.Host(broker, host =>
                {
                    // MassTransit reads TLS from the port and trusts any chain, so the scheme asks (ADR-079).
                    if (TransportSecurity.IsTls(broker))
                        host.UseSsl(ssl => ssl.EnforcePolicyErrors(SslPolicyErrors.RemoteCertificateChainErrors));
                });

                cfg.ReceiveEndpoint(
                    CompletionsQueue,
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

                        e.ConfigureConsumer<CommandConsumer<PersonalDataDeleteCompleted, RecordErasureCompletionCommand>>(
                            context);
                    });

                // No ConfigureEndpoints(context): it would give a consumer a queue with neither the inbox filter
                // nor the retry policy, and §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5). WaitUntilStarted
        // stays false, so a broker outage fails readiness rather than boot.
        return services;
    }
}
