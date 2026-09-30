using FluentValidation;
using Microsoft.Extensions.Time.Testing;

namespace Common.Application.Tests;

/// <summary>Domain-free stand-ins for §6.4's command and §6.5's query, so the pipeline is what is tested.</summary>
public sealed record Ping(string Message) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public Task<string> HandleAsync(Ping command, CancellationToken ct) =>
        Task.FromResult($"pong:{command.Message}");
}

public sealed record Ask(string Question) : IQuery<string>;

public sealed class AskHandler : IQueryHandler<Ask, string>
{
    public Task<string> HandleAsync(Ask query, CancellationToken ct) =>
        Task.FromResult($"answer:{query.Question}");
}

/// <summary>A command nothing handles — the §6.2 trap, made deliberate.</summary>
public sealed record Unhandled : ICommand<string>;

/// <summary>One request under two result types, so the invoker cache cannot key on the request alone.</summary>
public sealed record TwoResults : ICommand<string>, ICommand<int>;

public sealed class TwoResultsTextHandler : ICommandHandler<TwoResults, string>
{
    public Task<string> HandleAsync(TwoResults command, CancellationToken ct) =>
        Task.FromResult("text");
}

public sealed class TwoResultsNumberHandler : ICommandHandler<TwoResults, int>
{
    public Task<int> HandleAsync(TwoResults command, CancellationToken ct) =>
        Task.FromResult(42);
}

/// <summary>A command and a query under one result type, where a shared cache entry runs the wrong handler.</summary>
public sealed record BothWays : ICommand<string>, IQuery<string>;

public sealed class BothWaysCommandHandler : ICommandHandler<BothWays, string>
{
    public Task<string> HandleAsync(BothWays command, CancellationToken ct) =>
        Task.FromResult("command");
}

public sealed class BothWaysQueryHandler : IQueryHandler<BothWays, string>
{
    public Task<string> HandleAsync(BothWays query, CancellationToken ct) =>
        Task.FromResult("query");
}

/// <summary>A command whose handler throws, for the failure arm of §13.3.</summary>
public sealed record Boom : ICommand<string>;

public sealed class BoomHandler : ICommandHandler<Boom, string>
{
    public Task<string> HandleAsync(Boom command, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}

/// <summary>Advances the clock in the handler, so the duration §13.3 records is the test's choice.</summary>
public sealed record Tick : ICommand<string>;

public sealed class TickHandler(FakeTimeProvider clock) : ICommandHandler<Tick, string>
{
    public Task<string> HandleAsync(Tick command, CancellationToken ct)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        return Task.FromResult("ticked");
    }
}

/// <summary>A domain refusal, which §13.3 counts as an <c>ok</c> outcome.</summary>
public sealed record Reject : ICommand<Result>;

public sealed class RejectHandler : ICommandHandler<Reject, Result>
{
    public Task<Result> HandleAsync(Reject command, CancellationToken ct) =>
        Task.FromResult(Result.Failure(Error.Rule("test.rejected", "The domain said no.")));
}

/// <summary>The success arm of §6.3's failure guard, <c>Reject</c>'s counterpart.</summary>
public sealed record Approve : ICommand<Result>;

public sealed class ApproveHandler : ICommandHandler<Approve, Result>
{
    public Task<Result> HandleAsync(Approve command, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}

/// <summary>Replaces the scope's idempotency key inside §6.3's transaction, as a nested dispatch would.</summary>
public sealed record Reclaim(string Key) : ICommand<Result>;

public sealed class ReclaimHandler(IdempotencyContext idempotency) : ICommandHandler<Reclaim, Result>
{
    public Task<Result> HandleAsync(Reclaim command, CancellationToken ct)
    {
        idempotency.Claim(command.Key);
        return Task.FromResult(Result.Success());
    }
}

public sealed class PingValidator : AbstractValidator<Ping>
{
    public PingValidator() => RuleFor(x => x.Message).NotEmpty().WithErrorCode("Empty");
}

/// <summary>A second validator on <c>Ping</c>, since §6.3 runs every validator and gathers the failures.</summary>
public sealed class PingLengthValidator : AbstractValidator<Ping>
{
    public PingLengthValidator() =>
        RuleFor(x => x.Message).Must(message => message.Length >= 3).WithErrorCode("TooShort");
}

/// <summary>How many times a validator was constructed in one scope.</summary>
public sealed class ValidatorConstructions
{
    public int Count { get; private set; }

    public void Record() => Count++;
}

/// <summary>Counted, because §6.3 enumerates the injected validators twice.</summary>
public sealed class CountingValidator : AbstractValidator<Ask>
{
    public CountingValidator(ValidatorConstructions constructions) => constructions.Record();
}

/// <summary>Which behaviours ran, in entry and exit order; scoped, so a second scope starts empty.</summary>
public sealed class PipelineLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string entry) => _entries.Add(entry);
}

/// <summary>Logs entry and exit around <c>next()</c>, so a correct pipeline's log is a palindrome.</summary>
public abstract class RecordingBehavior<TRequest, TResult>(PipelineLog log)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = GetType().Name.Split('`')[0];

        log.Add($"enter {name}");
        TResult result = await next();
        log.Add($"leave {name}");

        return result;
    }
}

public sealed class FirstBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

public sealed class SecondBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

public sealed class ThirdBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

/// <summary>Constrained to commands as §6.3's transaction behaviour is, so a query omits it.</summary>
public sealed class CommandOnlyBehavior<TCommand, TResult>(PipelineLog log)
    : RecordingBehavior<TCommand, TResult>(log)
    where TCommand : ICommand<TResult>;

/// <summary>Returns without calling <c>next()</c> — the short-circuit arm.</summary>
public sealed class ShortCircuitBehavior<TRequest, TResult>(PipelineLog log)
    : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        log.Add("short-circuit");
        return Task.FromResult(default(TResult)!);
    }
}

/// <summary>Reports which scope built its handler, since §6.2's invoker cache lives for the process.</summary>
public sealed record WhichScope : ICommand<Guid>;

public sealed class ScopeMarker
{
    public Guid Id { get; } = Guid.CreateVersion7();
}

public sealed class WhichScopeHandler(ScopeMarker marker) : ICommandHandler<WhichScope, Guid>
{
    public Task<Guid> HandleAsync(WhichScope command, CancellationToken ct) =>
        Task.FromResult(marker.Id);
}
