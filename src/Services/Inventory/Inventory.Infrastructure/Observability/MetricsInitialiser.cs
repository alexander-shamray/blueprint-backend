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
/// <b>Public, not internal, for the same reason <c>Program</c> is (§4.2):</b>
/// the convention test that keeps this constructor honest names the type from
/// another assembly, and one access modifier is a smaller commitment than an
/// <c>InternalsVisibleTo</c> that has to name its consumer.
/// <para>
/// <b>The test for membership is not "is it a gauge" — it is "can this service
/// run for an hour without constructing it".</b> For every type below the
/// answer is yes. <see cref="RequestMetrics"/> is the worked example and the
/// one that nearly got left out: <c>LoggingBehavior</c> injects it and a
/// behaviour runs on every dispatched request, which is not the same as
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
    /// <b>§13.6's sample cannot be copied literally, and it fails two ways
    /// rather than one.</b> It declares a primary constructor whose parameters
    /// are named <c>_</c>, <c>__</c> and <c>___</c> and never read, which is
    /// <b>CS9113</b> — <i>parameter is unread</i> — three times over, and the
    /// discard-looking names do not escape it. Measured rather than assumed:
    /// CA1707, the rule a reader expects to fire on those names, does not.
    /// A guard is what turns a resolution into a read, and it is not ceremony
    /// here — a null would mean the container resolved a metrics type to
    /// nothing, which is the silent-instrument failure this class exists to
    /// prevent. The second way is on <see cref="StartAsync"/> below.
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
    // ADR-019 makes that an error. §13.6's sample spells it `ct` and fails the
    // build here for that reason — the other half of the finding above.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
