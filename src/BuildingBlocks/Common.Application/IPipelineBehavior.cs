namespace Common.Application;

/// <summary>The rest of the pipeline; not calling it short-circuits the request.</summary>
public delegate Task<TResult> NextDelegate<TResult>();

/// <summary>Nests outermost-first in registration order, so the §6.2 scan leaves it out (§6.3).</summary>
public interface IPipelineBehavior<in TRequest, TResult>
{
    Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct);
}
