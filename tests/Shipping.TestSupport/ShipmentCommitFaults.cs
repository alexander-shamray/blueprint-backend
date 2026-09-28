using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shipping.Domain.Shipments;

namespace Shipping.TestSupport;

/// <summary>
/// A commit that fails once, on demand, after the unit has moved its shipment
/// — the one rollback §6.3's execution strategy and the fulfilment pass's
/// per-row catch exist for, which no real fault produces on cue. Test support
/// only; the host never registers it.
/// </summary>
public sealed class ShipmentCommitFaults : SaveChangesInterceptor
{
    private CommitFault? _armed;

    /// <summary>
    /// Arms the next qualifying save. One fault at a time, because two armed
    /// at once would leave which of them fired to the order of the saves.
    /// </summary>
    public CommitFault Arm()
    {
        CommitFault fault = new(this);
        if (Interlocked.CompareExchange(ref _armed, fault, null) is not null)
            throw new InvalidOperationException("A commit fault is already armed.");

        return fault;
    }

    internal void Disarm(CommitFault fault) => Interlocked.CompareExchange(ref _armed, null, fault);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only a save that moves a shipment qualifies. This service stages no
        // outbox row, so the condition Payments' interceptor reads is never
        // true here; an inbox write, a stored address or an arriving shipment
        // is not the unit whose rollback is being asked for, and firing on one
        // would make Fired a claim about nothing.
        bool moving = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<Shipment>().Any(e => e.State == EntityState.Modified);

        if (!moving)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        fault.Fired = true;

        // DbUpdateException rather than a TimeoutException, which
        // SqlServerTransientExceptionDetector accepts: §6.3's strategy would
        // retry the unit whole, and since Arm disarms on fire the second
        // attempt commits. The carrier call is outside CommitAsync, so a
        // retried unit would book nothing and commit cleanly. Non-transient,
        // the exception leaves ExecuteAsync and reaches the pass's per-row
        // catch, which is what a crash between the carrier's answer and the
        // commit actually does.
        throw new DbUpdateException("Injected commit fault.");
    }
}

/// <summary>
/// One armed fault. Disposing disarms it, so a unit that never reached a
/// qualifying save cannot leave it primed for whatever runs next.
/// </summary>
public sealed class CommitFault : IDisposable
{
    private readonly ShipmentCommitFaults _owner;

    internal CommitFault(ShipmentCommitFaults owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
