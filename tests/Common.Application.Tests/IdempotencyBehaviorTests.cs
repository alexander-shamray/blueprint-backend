using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§8.5's behaviour, driven directly so a failure cannot be attributed to another behaviour.</summary>
public class IdempotencyBehaviorTests
{
    private static readonly Guid Caller = Guid.Parse("0195e4b2-0000-7000-8000-00000000000a");
    private static readonly Guid Other = Guid.Parse("0195e4b2-0000-7000-8000-00000000000b");
    private static readonly Guid Command = Guid.Parse("0195e4b2-0000-7000-8000-0000000000ff");

    private static IdempotencyBehavior<ProtectedCommand, Result<Guid>> Behaviour(
        RecordingIdempotencyStore store,
        ICurrentUser? user = null,
        IdempotencyContext? idempotency = null) =>
        new(store, user ?? StubCurrentUser.Authenticated(Caller), idempotency ?? new IdempotencyContext());

    [Fact]
    public async Task A_first_attempt_claims_the_key_runs_the_handler_and_records_the_outcome()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;
        Guid placed = Guid.CreateVersion7();

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success(placed));
            },
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe(placed);
        handlerRuns.ShouldBe(1);
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"complete {ExpectedKey}"]);
        store.Entries[ExpectedKey].ShouldBe(new IdempotencyEntry(false, $"\"{placed}\""));
    }

    [Fact]
    public async Task The_outcome_is_recorded_under_the_token_the_claim_returned()
    {
        RecordingIdempotencyStore store = new();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.MintedToken.ShouldNotBeNull();
        store.WrittenUnder.ShouldBe(store.MintedToken);
    }

    [Fact]
    public async Task The_release_after_a_fault_carries_the_token_the_claim_returned()
    {
        RecordingIdempotencyStore store = new();

        await Should.ThrowAsync<InvalidOperationException>(
            Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("boom"),
                TestContext.Current.CancellationToken));

        store.MintedToken.ShouldNotBeNull();
        store.WrittenUnder.ShouldBe(store.MintedToken);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_retry_of_a_completed_command_replays_the_value_without_running_the_handler()
    {
        RecordingIdempotencyStore store = new();
        Guid placed = Guid.CreateVersion7();
        store.Completed(ExpectedKey, $"\"{placed}\"");
        int handlerRuns = 0;

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success(Guid.CreateVersion7()));
            },
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(0, "the handler must not run on a replay");
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(placed, "the replayed value is the first attempt's, not a new one");
    }

    [Fact]
    public async Task A_void_command_replays_a_success_carrying_no_value()
    {
        RecordingIdempotencyStore store = new();
        store.Completed(VoidKey, "null");
        int handlerRuns = 0;

        IdempotencyBehavior<VoidProtectedCommand, Result> behaviour =
            new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

        Result result = await behaviour.HandleAsync(
            new VoidProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success());
            },
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(0);
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_request_while_the_first_is_in_flight_is_refused()
    {
        RecordingIdempotencyStore store = new();
        store.InFlight(ExpectedKey);

        ConcurrentRequestException thrown = await Should.ThrowAsync<ConcurrentRequestException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken));

        thrown.CommandId.ShouldBe(Command);
    }

    [Fact]
    public async Task An_entry_that_vanished_between_the_claim_and_the_read_is_refused()
    {
        RecordingIdempotencyStore store = new();
        VanishingStore vanishing = new(store);

        await Should.ThrowAsync<ConcurrentRequestException>(
            () => new IdempotencyBehavior<ProtectedCommand, Result<Guid>>(
                    vanishing,
                    StubCurrentUser.Authenticated(Caller),
                    new IdempotencyContext())
                .HandleAsync(
                    new ProtectedCommand(Command),
                    () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_handler_that_throws_releases_the_claim_and_the_original_fault_survives()
    {
        RecordingIdempotencyStore store = new();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("handler exploded"),
                TestContext.Current.CancellationToken));

        thrown.Message.ShouldBe("handler exploded");
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"release {ExpectedKey}"]);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_failed_Result_releases_the_claim()
    {
        RecordingIdempotencyStore store = new();

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Failure<Guid>(Error.Rule("test.refused", "No."))),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"release {ExpectedKey}"]);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_store_failure_after_the_handler_holds_the_claim_rather_than_releasing_it()
    {
        // §8.5's release table: a CompleteAsync fault holds the claim.
        RecordingIdempotencyStore store = new()
        {
            CompleteFault = new TimeoutException("redis went away")
        };

        await Should.ThrowAsync<TimeoutException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken));

        store.Calls.ShouldBe(
            [$"claim {ExpectedKey}", $"complete {ExpectedKey}"],
            "a fault raised after the transaction committed must not release the claim");
    }

    [Fact]
    public async Task The_key_carries_the_authenticated_subject_and_not_the_command()
    {
        RecordingIdempotencyStore store = new();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.Calls[0].ShouldBe($"claim {Caller}:{ProtectedCommand.OperationName}:{Command}");
    }

    [Fact]
    public async Task Two_subjects_sending_one_CommandId_do_not_collide()
    {
        RecordingIdempotencyStore store = new();
        Guid mine = Guid.CreateVersion7();
        Guid theirs = Guid.CreateVersion7();

        Result<Guid> first = await Behaviour(store, StubCurrentUser.Authenticated(Caller)).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(mine)),
            TestContext.Current.CancellationToken);

        Result<Guid> second = await Behaviour(store, StubCurrentUser.Authenticated(Other)).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(theirs)),
            TestContext.Current.CancellationToken);

        first.Value.ShouldBe(mine);
        second.Value.ShouldBe(theirs, "the second caller ran their own command rather than replaying the first's");
        store.Entries.Count.ShouldBe(2, "one key per subject");
    }

    [Fact]
    public async Task A_caller_with_no_principal_claims_under_the_shared_system_segment()
    {
        // A residual §8.5 argues, pinned so it cannot change unnoticed.
        RecordingIdempotencyStore store = new();

        await Behaviour(store, StubCurrentUser.Anonymous()).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.Calls[0].ShouldBe($"claim system:{ProtectedCommand.OperationName}:{Command}");
    }

    [Fact]
    public void The_operation_segment_is_declared_and_is_not_the_type_name()
    {
        ProtectedCommand.OperationName.ShouldNotBe(nameof(ProtectedCommand));
        ProtectedCommand.OperationName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_command_that_does_not_opt_in_runs_unprotected_and_says_nothing()
    {
        // The container silently omits an open generic whose constraints the closed type fails (§8.5).
        RecordingIdempotencyStore store = new();

        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddSingleton<IIdempotencyStore>(store);
            services.AddSingleton<ICurrentUser>(StubCurrentUser.Authenticated(Caller));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result result = await dispatcher.SendAsync(
            new UnprotectedCommand(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Calls.ShouldBeEmpty("the behaviour was never selected, and nothing said so");
    }

    [Fact]
    public async Task The_completion_is_made_with_None_rather_than_the_callers_token()
    {
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            cancelled.Token);

        // The claim is the positive control: the indexer throws for a call never recorded.
        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["complete"].ShouldBe(CancellationToken.None);
    }

    [Fact]
    public async Task The_release_after_a_refusal_is_made_with_None_rather_than_the_callers_token()
    {
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Failure<Guid>(Error.Rule("test.refused", "No."))),
            cancelled.Token);

        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["release"].ShouldBe(CancellationToken.None);
    }

    [Fact]
    public async Task The_release_after_a_thrown_handler_is_made_with_None_rather_than_the_callers_token()
    {
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("handler exploded"),
                cancelled.Token));

        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["release"].ShouldBe(CancellationToken.None);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_claimed_key_is_published_for_the_transaction_to_mark()
    {
        // Read inside next(), where §6.3 opens its transaction; the key is cleared on the way out.
        RecordingIdempotencyStore store = new();
        IdempotencyContext idempotency = new();
        string? seen = null;

        await Behaviour(store, idempotency: idempotency).HandleAsync(
            new ProtectedCommand(Command),
            () =>
            {
                seen = idempotency.Key;
                return Task.FromResult(Result.Success(Guid.CreateVersion7()));
            },
            TestContext.Current.CancellationToken);

        seen.ShouldBe(ExpectedKey);
    }

    [Theory]
    [InlineData(nameof(Outcome.Success))]
    [InlineData(nameof(Outcome.Failure))]
    [InlineData(nameof(Outcome.Throws))]
    public async Task The_key_does_not_outlive_the_dispatch_that_claimed_it(string outcome)
    {
        RecordingIdempotencyStore store = new();
        IdempotencyContext idempotency = new();

        Task<Result<Guid>> dispatch = Behaviour(store, idempotency: idempotency).HandleAsync(
            new ProtectedCommand(Command),
            () => outcome switch
            {
                nameof(Outcome.Success) => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                nameof(Outcome.Failure) => Task.FromResult(
                    Result.Failure<Guid>(Error.Rule("test.refused", "The domain said no."))),
                _ => throw new InvalidOperationException("handler exploded")
            },
            TestContext.Current.CancellationToken);

        if (outcome == nameof(Outcome.Throws))
            await Should.ThrowAsync<InvalidOperationException>(() => dispatch);
        else
            await dispatch;

        idempotency.Key.ShouldBeNull();
    }

    private enum Outcome
    {
        Success,
        Failure,
        Throws
    }

    [Fact]
    public async Task A_replay_publishes_no_key_because_it_opens_no_transaction()
    {
        RecordingIdempotencyStore store = new();
        store.Completed(ExpectedKey, $"\"{Guid.CreateVersion7()}\"");
        IdempotencyContext idempotency = new();

        Result<Guid> result = await Behaviour(store, idempotency: idempotency).HandleAsync(
            new ProtectedCommand(Command),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        idempotency.Key.ShouldBeNull();
    }

    private static string ExpectedKey => $"{Caller}:{ProtectedCommand.OperationName}:{Command}";

    private static string VoidKey => $"{Caller}:{VoidProtectedCommand.OperationName}:{Command}";

    /// <summary>Reports the key held, then absent: an expiry between the claim and the read.</summary>
    private sealed class VanishingStore(IIdempotencyStore inner) : IIdempotencyStore
    {
        public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<IdempotencyEntry?>(null);

        public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
            inner.CompleteAsync(key, claim, payload, ct);

        public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
            inner.ReleaseAsync(key, claim, ct);

        public Task<IReadOnlyCollection<string>> UnheldAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken ct) =>
            inner.UnheldAsync(keys, ct);
    }
}

/// <summary>An opted-in command returning a value — §6.4's shape.</summary>
public sealed record ProtectedCommand(Guid CommandId) : ICommand<Result<Guid>>, IIdempotentCommand
{
    public static string OperationName => "tests.protected";
}

public sealed class ProtectedCommandHandler : ICommandHandler<ProtectedCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(ProtectedCommand command, CancellationToken ct) =>
        Task.FromResult(Result.Success(Guid.CreateVersion7()));
}

/// <summary>An opted-in command returning nothing — the <c>NoValue</c> path.</summary>
public sealed record VoidProtectedCommand(Guid CommandId) : ICommand<Result>, IIdempotentCommand
{
    public static string OperationName => "tests.void";
}

/// <summary>Satisfies the behaviour's result constraint but not <see cref="IIdempotentCommand"/>.</summary>
public sealed record UnprotectedCommand : ICommand<Result>;

public sealed class UnprotectedCommandHandler : ICommandHandler<UnprotectedCommand, Result>
{
    public Task<Result> HandleAsync(UnprotectedCommand command, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}
