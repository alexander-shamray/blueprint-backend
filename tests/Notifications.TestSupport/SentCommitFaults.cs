using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Notifications.Application.Records;

namespace Notifications.TestSupport;

/// <summary>Fails, once and on demand, the commit marking a notice sent: the crash after the relay's accept.</summary>
public sealed class SentCommitFaults : SaveChangesInterceptor
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
        // Only the completion qualifies; the intent and the customer's assignment move a notice too.
        bool completing = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<Notification>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == NotificationStatus.Sent);

        if (!completing)
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
    private readonly SentCommitFaults _owner;

    internal CommitFault(SentCommitFaults owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
