namespace Common.Application;

/// <summary>Kept apart from <see cref="ICommand{TResult}"/> so a query never opens a transaction (§6.3).</summary>
public interface IQuery<out TResult>;

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}
