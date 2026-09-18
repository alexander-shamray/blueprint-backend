using Common.Application;
using Common.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// §6.3's transaction boundary over the Inventory context. Internal — nothing
/// outside this assembly resolves it by type, only through
/// <see cref="IUnitOfWork"/>.
/// </summary>
internal sealed class EfUnitOfWork(InventoryDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        // The token-aware overload, so cancellation is observed by the strategy
        // itself. With the parameterless one the token reaches only the calls
        // inside the delegate, so a cancel during a retry backoff is not seen
        // until the delay elapses and the next attempt happens to reach one.
        return await strategy.ExecuteAsync(
            async token =>
            {
                // Every attempt starts from committed state. EF does not reset
                // the change tracker when a transaction rolls back, so without
                // this line a retry re-runs the domain method on attempt 1's
                // tracked, already-mutated aggregates out of the identity map,
                // and one SaveChanges commits the mutation twice.
                db.ChangeTracker.Clear();

                await using IDbContextTransaction tx =
                    await db.Database.BeginTransactionAsync(token);
                TResult result = await operation(token);

                // The commit decision belongs with the commit. §6.3's behaviour
                // declines to SaveChanges on a failed Result — but ExecuteRawAsync
                // writes on this transaction's connection immediately, and only a
                // rollback undoes that. Returning without committing disposes the
                // transaction, which rolls it back.
                if (result is Result { IsFailure: true })
                {
                    // And the tracker is cleared with it, because a rollback that
                    // leaves the rejected mutations tracked is only half a
                    // rollback. This line used to be unnecessary and the comment
                    // above used to say so: "declines to SaveChanges … which is
                    // enough for tracked changes" was true while this behaviour
                    // was the only thing that called SaveChanges on the scope.
                    //
                    // §9.5's inbox filter is the second caller. It runs after the
                    // consumer returns and saves unconditionally — it has its own
                    // row to write — so anything a rejected handler left tracked
                    // would be persisted by it, outside the transaction that was
                    // just rolled back. A domain refusal would commit its own
                    // mutations, which is the one outcome §6.3 exists to prevent.
                    db.ChangeTracker.Clear();

                    return result;
                }

                await tx.CommitAsync(token);
                return result;
            },
            ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // Owned children are not roots and do not count — that is the difference
    // between an aggregate and a table (§6.3, principle 3).
    public int ModifiedAggregateCount => db.ChangeTracker
        .Entries()
        .Count(e => e.Entity is IAggregateRoot &&
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    // The transaction's own connection and transaction, explicitly passed —
    // this is what makes a raw write part of the command rather than beside it.
    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct)
    {
        // Null-conditional here would hand Dapper transaction: null, and a
        // command with no transaction autocommits — so the one call this member
        // exists to prevent would succeed silently, on its own connection,
        // outside the unit the caller believes it is in. The rule is the same
        // one ModifiedAggregateCount is checked by rather than trusted with
        // (§6.3): a convention nothing enforces is a convention that fails on
        // the first handler that has not read the comment.
        IDbContextTransaction transaction = db.Database.CurrentTransaction ??
            throw new InvalidOperationException(
                "ExecuteRawAsync was called outside IUnitOfWork.ExecuteAsync. The write would commit " +
                "immediately on its own connection, outside the command's transaction (§6.3).");

        return db.Database.GetDbConnection().ExecuteAsync(
            new CommandDefinition(
                sql,
                parameters,
                transaction: transaction.GetDbTransaction(),
                cancellationToken: ct));
    }
}
