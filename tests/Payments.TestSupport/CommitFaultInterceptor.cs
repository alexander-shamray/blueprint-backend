using System.Runtime.CompilerServices;
using Common.Domain;
using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Payments.TestSupport;

/// <summary>A commit that fails once, on demand, after the unit has staged its outbox row or its audit row.</summary>
/// <remarks>
/// Thrown once the save's statements have run inside the unit's transaction, so the outbox or audit row it wrote is
/// gone only if the transaction rolls back (§6.3); thrown before the save, that row would never have been written.
/// </remarks>
public sealed class CommitFaultInterceptor : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, CommitFault> _pending = [];

    private CommitFault? _armed;

    /// <summary>Arms the next save that stages an outbox or an erasure row, one fault at a time.</summary>
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
        // Only a save with an outbox row or an erasure row staged qualifies, so Fired is a claim about the unit.
        bool staged = eventData.Context is not null &&
            (eventData.Context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added) ||
                eventData.Context.ChangeTracker.Entries<PersonalDataErasure>().Any(e => e.State == EntityState.Added));

        if (!staged)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is not null)
            _pending.AddOrUpdate(eventData.Context!, fault);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is null || !_pending.TryGetValue(eventData.Context, out CommitFault? fault))
            return base.SavedChangesAsync(eventData, result, cancellationToken);

        _pending.Remove(eventData.Context);
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
