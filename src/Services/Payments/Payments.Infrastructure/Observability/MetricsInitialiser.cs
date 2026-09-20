using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Payments.Infrastructure.Provider;

namespace Payments.Infrastructure.Observability;

/// <summary>
/// Constructs every metrics type at startup, because a singleton registration
/// alone is lazy and an instrument never constructed does not exist (§13.6).
/// </summary>
/// <remarks>
/// Membership asks "can this service run for an hour without constructing
/// it". <see cref="ProviderMetrics"/> is the worked example: the adapter
/// injects it, so a Payments that has authorised nothing would report nothing
/// where §13.6 wants zero. Public for the reason <c>Program</c> is (§4.2).
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

    // `cancellationToken`, not this repository's usual `ct`: CA1725 requires an
    // implementation's parameter name to match the interface it implements, and
    // ADR-019 makes that an error. §13.6's sample spells it `ct` and fails the
    // build here for that reason — the other half of the finding above.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
