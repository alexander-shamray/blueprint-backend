using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Payments.TestSupport;

/// <summary>A commit that fails once, on demand, after the unit has staged its outbox row.</summary>
public sealed class CommitFaultInterceptor : SaveChangesInterceptor
{
    private CommitFault? _armed;

    /// <summary>Arms the next save that stages an outbox row, one fault at a time.</summary>
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
        // Only a save with an outbox row staged qualifies, so Fired is a claim about the unit under test.
        bool staged = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added);

        if (!staged)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        fault.Fired = true;

        // Transient to SQL Server's execution strategy and to §9.8's policy alike, so the unit is retried whole.
        throw new TimeoutException("Injected commit fault.");
    }
}

/// <summary>One armed fault; disposing disarms it, so an unfired fault cannot prime whatever runs next.</summary>
public sealed class CommitFault : IDisposable
{
    private readonly CommitFaultInterceptor _owner;

    internal CommitFault(CommitFaultInterceptor owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
