namespace Common.Application;

/// <summary>The entry point for commands and queries, in place of a mediator library (ADR-004).</summary>
public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}
