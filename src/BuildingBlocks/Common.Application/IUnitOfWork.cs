namespace Common.Application;

/// <summary>The command transaction boundary, implemented over EF Core in Infrastructure.</summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    /// <summary>Distinct aggregate roots with pending changes.</summary>
    int ModifiedAggregateCount { get; }

    /// <summary>Runs <paramref name="operation"/> atomically, retrying the whole unit on transient faults.</summary>
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);

    /// <summary>Raw SQL on the transaction's connection, for a table with no aggregate (§9.6).</summary>
    Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct);
}
