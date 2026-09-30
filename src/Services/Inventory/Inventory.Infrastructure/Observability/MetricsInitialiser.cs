using Common.Application;
using Common.Infrastructure.Messaging;
using Inventory.Application.Reservations;
using Microsoft.Extensions.Hosting;

namespace Inventory.Infrastructure.Observability;

/// <summary>Builds every metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
/// <remarks>
/// A type belongs if the service can run for an hour without constructing it, as <see cref="RequestMetrics"/> can
/// when no request is dispatched. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards make it a read (§13.6).</summary>
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

    // `cancellationToken`, not `ct`: CA1725 keeps the interface's name, an error under ADR-019.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
