using Common.Application;
using Common.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>§6.3's transaction boundary over the Inventory context.</summary>
internal sealed class EfUnitOfWork(InventoryDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        // The token-aware overload, so a cancel during a retry backoff is seen by the strategy itself.
        return await strategy.ExecuteAsync(
            async token =>
            {
                // A rollback leaves the tracker as it was, so a retry would re-run on mutated aggregates.
                db.ChangeTracker.Clear();

                await using IDbContextTransaction tx =
                    await db.Database.BeginTransactionAsync(token);
                TResult result = await operation(token);

                // Declining to SaveChanges is not enough: ExecuteRawAsync writes on this transaction's connection
                // immediately, and only a rollback undoes that. Returning uncommitted disposes, so rolls back.
                if (result is Result { IsFailure: true })
                {
                    // Cleared too, since §9.5's inbox filter saves afterwards and would commit what was rolled back.
                    db.ChangeTracker.Clear();

                    return result;
                }

                await tx.CommitAsync(token);
                return result;
            },
            ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // Owned children are not roots and do not count (§6.3).
    public int ModifiedAggregateCount => db.ChangeTracker
        .Entries()
        .Count(e => e.Entity is IAggregateRoot &&
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    // The transaction's own connection and transaction, so a raw write is part of the command.
    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct)
    {
        // Throws rather than passing a null transaction, with which Dapper would autocommit outside the unit.
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
