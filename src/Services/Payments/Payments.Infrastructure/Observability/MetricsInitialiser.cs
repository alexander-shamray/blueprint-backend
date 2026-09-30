using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Payments.Infrastructure.Provider;

namespace Payments.Infrastructure.Observability;

/// <summary>Builds every metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
/// <remarks>
/// <see cref="ProviderMetrics"/> belongs: the adapter injects it, so a Payments that has authorised nothing would
/// report nothing where §13.6 wants zero. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards make it a read (§13.6).</summary>
    public MetricsInitialiser(
        OutboxMetrics outbox,
        MessagingMetrics messaging,
        RequestMetrics requests,
        ProviderMetrics provider)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(provider);
    }

    // `cancellationToken`, not `ct`: CA1725 keeps the interface's name, an error under ADR-019.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
