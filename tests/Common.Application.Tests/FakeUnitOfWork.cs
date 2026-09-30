namespace Common.Application.Tests;

/// <summary>Logs every member the behaviour touches to the shared <see cref="PipelineLog"/>.</summary>
public sealed class FakeUnitOfWork(PipelineLog log) : IUnitOfWork
{
    /// <summary>What <see cref="ModifiedAggregateCount"/> reports.</summary>
    public int AggregateCount { get; set; }

    public bool HasActiveTransaction { get; set; }

    public int ModifiedAggregateCount
    {
        get
        {
            log.Add("count");
            return AggregateCount;
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        log.Add("execute");
        return await operation(ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        log.Add("save");
        return Task.FromResult(0);
    }

    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct)
    {
        log.Add("raw");
        return Task.CompletedTask;
    }
}
