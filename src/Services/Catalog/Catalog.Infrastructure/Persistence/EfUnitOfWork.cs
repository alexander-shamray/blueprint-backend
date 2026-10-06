using Common.Application;
using Common.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§6.3's transaction boundary, resolved only through <see cref="IUnitOfWork"/>.</summary>
internal sealed class EfUnitOfWork(CatalogDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        // The token-aware overload, so a cancel during a retry backoff is observed by the strategy itself.
        return await strategy.ExecuteAsync(
            async token =>
            {
                // Every attempt starts from committed state: EF keeps a rolled-back attempt's mutated
                // aggregates tracked, and a retry would commit the mutation twice.
                db.ChangeTracker.Clear();

                await using IDbContextTransaction tx =
                    await db.Database.BeginTransactionAsync(token);
                TResult result = await operation(token);

                // §6.3's behaviour declines to SaveChanges on a failed Result, but ExecuteRawAsync writes on this
                // transaction's connection immediately, and only a rollback undoes that. Returning uncommitted
                // disposes the transaction, which rolls it back.
                if (result is Result { IsFailure: true })
                {
                    // Cleared too, because §9.5's inbox filter saves unconditionally after the consumer returns
                    // and would commit what a rejected handler left tracked.
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
        // Not null-conditional: Dapper given no transaction autocommits on its own connection, outside the unit.
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
