using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Payments.TestSupport;

/// <summary>
/// A commit that fails once, on demand, after the unit has staged its outbox
/// row — the one rollback §6.3's execution strategy and §9.8's retry exist
/// for, which no real fault produces on cue. Test support only; the host
/// never registers it.
/// </summary>
public sealed class CommitFaultInterceptor : SaveChangesInterceptor
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
        // Only a save with an outbox row staged qualifies: an inbox write or
        // an event's record is not the unit whose rollback is being asked
        // for, and firing on one would make Fired a claim about nothing.
        bool staged = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added);

        if (!staged)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        fault.Fired = true;

        // TimeoutException because it is transient to SQL Server's execution
        // strategy and to §9.8's policy alike, so the unit is retried whole.
        throw new TimeoutException("Injected commit fault.");
    }
}

/// <summary>
/// One armed fault. Disposing disarms it, so a unit that never reached a
/// qualifying save cannot leave it primed for whatever runs next.
/// </summary>
public sealed class CommitFault : IDisposable
{
    private readonly CommitFaultInterceptor _owner;

    internal CommitFault(CommitFaultInterceptor owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
