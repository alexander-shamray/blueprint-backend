using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;

namespace Ordering.Infrastructure.Observability;

/// <summary>Builds every metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
/// <remarks>
/// <c>OrderMetrics</c> is absent because it does not exist yet: it arrives with §6.6's <c>OrderSummaries</c>
/// projection, its only call site (§13.3). Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards make it a read (§13.6).</summary>
    public MetricsInitialiser(OutboxMetrics outbox, MessagingMetrics messaging, RequestMetrics requests)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
    }

    // `cancellationToken`, not `ct`: CA1725 keeps the interface's name, an error under ADR-019.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
