using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Notifications.Application.Records;

namespace Notifications.TestSupport;

/// <summary>Fails, once and on demand, the commit marking a notice sent: the crash after the relay's accept.</summary>
public sealed class SentCommitFaults : SaveChangesInterceptor
{
    private CommitFault? _armed;
    private CommitFault? _committed;

    /// <summary>The transaction half of <c>afterCommit</c>: the acknowledgement that is lost.</summary>
    public DbTransactionInterceptor Acknowledgements { get; }

    public SentCommitFaults() => Acknowledgements = new LostAcknowledgements(this);

    /// <summary>Arms the next qualifying save, one fault at a time, so which one fired is never in doubt.</summary>
    /// <param name="afterCommit">Lets the commit through and fails its acknowledgement, a retriable fault.</param>
    public CommitFault Arm(bool afterCommit = false)
    {
        CommitFault fault = new(this, afterCommit);
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
            eventData.Context.ChangeTracker
                .Entries<Notification>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == NotificationStatus.Sent);

        if (!completing)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        if (fault.AfterCommit)
        {
            _committed = fault;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        fault.Fired = true;

        // Non-transient, so §6.3's strategy does not retry the unit and the fault reaches the pass's per-row catch.
        throw new DbUpdateException("Injected commit fault.");
    }

    // A timeout, which the production strategy retries: the commit is durable and the unit runs again (§6.3).
    private sealed class LostAcknowledgements(SentCommitFaults owner) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommitFault? fault = Interlocked.Exchange(ref owner._committed, null);
            if (fault is null)
                return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);

            fault.Fired = true;
            throw new TimeoutException("Injected lost acknowledgement.");
        }
    }
}

/// <summary>One armed fault; disposing disarms it, so an unfired fault cannot reach whatever runs next.</summary>
public sealed class CommitFault : IDisposable
{
    private readonly SentCommitFaults _owner;

    internal CommitFault(SentCommitFaults owner, bool afterCommit)
    {
        _owner = owner;
        AfterCommit = afterCommit;
    }

    internal bool AfterCommit { get; }

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
