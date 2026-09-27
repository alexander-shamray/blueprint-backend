using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;

namespace Catalog.Infrastructure.Observability;

/// <summary>
/// Constructs every metrics type at startup, because a singleton registration
/// alone is lazy and an instrument never constructed does not exist (§13.6).
/// </summary>
/// <remarks>
/// Membership asks "can this service run for an hour without constructing
/// it". <see cref="RequestMetrics"/> is the worked example: §6.3's
/// <c>LoggingBehavior</c> injects it, so a service with no traffic would
/// report nothing where §13.6 wants zero. Public, as <c>Program</c> is (§4.2).
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>
    /// Resolving the parameters is the entire job — constructing each one
    /// registers its instruments with its meter — so nothing is kept.
    /// </summary>
    /// <remarks>
    /// §13.6's sample names its parameters <c>_</c>, <c>__</c> and <c>___</c>
    /// and never reads them, which is CS9113 three times over. The guards are
    /// what turn a resolution into a read, and a null would mean the container
    /// resolved a metrics type to nothing.
    /// </remarks>
    public MetricsInitialiser(OutboxMetrics outbox, MessagingMetrics messaging, RequestMetrics requests)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
    }

    // `cancellationToken`, not this repository's usual `ct`: CA1725 requires an
    // implementation's parameter name to match the interface it implements, and
    // ADR-019 makes that an error. §13.6's sample spells it `ct` and fails the
    // build here for that reason — the other half of the finding above.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
