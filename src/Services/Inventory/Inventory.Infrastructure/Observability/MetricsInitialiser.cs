using Common.Application;
using Common.Infrastructure.Messaging;
using Inventory.Application.Reservations;
using Microsoft.Extensions.Hosting;

namespace Inventory.Infrastructure.Observability;

/// <summary>
/// Constructs every metrics type at startup, because a singleton registration
/// alone is lazy and an instrument that is never constructed does not exist
/// (§13.6).
/// </summary>
/// <remarks>
/// Public, not internal, for the same reason <c>Program</c> is (§4.2): what
/// keeps this constructor honest lives in another assembly, and one access
/// modifier is a smaller commitment than an <c>InternalsVisibleTo</c> that has
/// to name its consumer.
/// <para>
/// The test for membership is not "is it a gauge" — it is "can this service
/// run for an hour without constructing it". For every type below the answer
/// is yes. <see cref="RequestMetrics"/> is the worked example:
/// <c>LoggingBehavior</c> injects it and a behaviour runs on every
/// dispatched request, which is not the same as
/// something having constructed it — a health probe is mapped by
/// <c>MapHealthChecks</c> (§13.5) and never enters the pipeline, and a canary
/// before cutover or a service whose traffic has simply stopped publishes
/// nothing at all.
/// </para>
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>
    /// Resolving the parameters is the entire job — constructing each one
    /// registers its instruments with its meter — so nothing is kept.
    /// </summary>
    /// <remarks>
    /// The guard on each parameter is what turns a resolution into a read — a
    /// constructor parameter that is never read is CS9113, an error under
    /// ADR-019 — and it is not ceremony either way: a null would mean the
    /// container resolved a metrics type to nothing, which is the
    /// silent-instrument failure this class exists to prevent.
    /// </remarks>
    public MetricsInitialiser(
        OutboxMetrics outbox,
        MessagingMetrics messaging,
        RequestMetrics requests,
        InventoryMetrics reservations)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(reservations);
    }

    // `cancellationToken`, not this repository's usual `ct`: CA1725 requires an
    // implementation's parameter name to match the interface it implements, and
    // ADR-019 makes that an error.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
