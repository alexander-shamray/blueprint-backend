namespace Common.Application;

/// <summary>A request that changes state; one the domain may refuse returns a <see cref="Result"/> (§6.4).</summary>
public interface ICommand<out TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}
