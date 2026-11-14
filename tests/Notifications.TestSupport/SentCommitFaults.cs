using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Notifications.Application.Records;

namespace Notifications.TestSupport;

/// <summary>Fails, once and on demand, the commit marking a notice sent: the crash after the relay's accept.</summary>
public sealed class SentCommitFaults : SaveChangesInterceptor
{
    private CommitFault? _armed;
    private Parked? _committed;

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

    // A parked fault whose save never committed is dropped with it, or it would fire on another test's commit.
    internal void Disarm(CommitFault fault)
    {
        Interlocked.CompareExchange(ref _armed, null, fault);
        if (Volatile.Read(ref _committed) is { } parked && parked.Fault == fault)
            Interlocked.CompareExchange(ref _committed, null, parked);
    }

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
            Volatile.Write(ref _committed, new Parked(fault, eventData.Context));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        fault.Fired = true;

        // Non-transient, so §6.3's strategy does not retry the unit and the fault reaches the pass's per-row catch.
        throw new DbUpdateException("Injected commit fault.");
    }

    // A save that failed or was cancelled never commits, as Payments' CommitFaultInterceptor reads it too.
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    private void Drop(DbContext? context)
    {
        if (Volatile.Read(ref _committed) is { } parked && parked.Context == context)
            Interlocked.CompareExchange(ref _committed, null, parked);
    }

    private sealed record Parked(CommitFault Fault, DbContext? Context);

    // A timeout, which the production strategy retries: the commit is durable and the unit runs again (§6.3).
    private sealed class LostAcknowledgements(SentCommitFaults owner) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            // Only the commit of the context that parked it, never another on the shared collection host.
            if (Volatile.Read(ref owner._committed) is not { } parked || parked.Context != eventData.Context ||
                Interlocked.CompareExchange(ref owner._committed, null, parked) != parked)
            {
                return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            }

            parked.Fault.Fired = true;
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
