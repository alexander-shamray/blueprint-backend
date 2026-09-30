using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shipping.Domain.Shipments;

namespace Shipping.TestSupport;

/// <summary>A commit that fails once, on demand, after the unit has moved its shipment (§6.3).</summary>
public sealed class ShipmentCommitFaults : SaveChangesInterceptor
{
    private CommitFault? _armed;

    /// <summary>Arms the next qualifying save, one fault at a time, so which one fired is never in doubt.</summary>
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
        // Only a save that moves a shipment qualifies, so Fired is a claim about that unit.
        bool moving = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<Shipment>().Any(e => e.State == EntityState.Modified);

        if (!moving)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        fault.Fired = true;

        // Non-transient, so §6.3's strategy does not retry the unit and the fault reaches the pass's per-row catch.
        throw new DbUpdateException("Injected commit fault.");
    }
}

/// <summary>One armed fault; disposing disarms it, so an unfired fault cannot reach whatever runs next.</summary>
public sealed class CommitFault : IDisposable
{
    private readonly ShipmentCommitFaults _owner;

    internal CommitFault(ShipmentCommitFaults owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
