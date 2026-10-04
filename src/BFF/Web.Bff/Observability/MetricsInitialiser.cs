using Common.Infrastructure.Messaging;

namespace Web.Bff.Observability;

/// <summary>Constructs each metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards are the read CS9113 asks for.</summary>
    public MetricsInitialiser(MessagingMetrics messaging, ProjectionMetrics projection)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(projection);
    }

    // `cancellationToken`, not `ct`: CA1725 matches the interface's parameter name, and ADR-019 makes it an error.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
