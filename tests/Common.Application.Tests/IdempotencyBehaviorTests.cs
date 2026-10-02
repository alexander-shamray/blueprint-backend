using System.Security.Cryptography;
using System.Text;
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
        store.Entries[ExpectedKey].ShouldBe(
            new IdempotencyEntry(false, $"sha256:{FingerprintOf(BareJson)}:\"{placed}\""),
            "the value is stored behind the fingerprint of the command that produced it (ADR-057)");
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

    [Fact]
    public async Task A_retry_carrying_the_same_command_replays_the_first_result()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result<string>> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success($"order-{handlerRuns}"));
        }

        Result<string> first = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        Result<string> second = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(1, "an equal command under a completed key is the first one repeated");
        second.Value.ShouldBe(first.Value);
        store.Calls.ShouldBe(
            [$"claim {ContentKey}", $"complete {ContentKey}", $"claim {ContentKey}", $"get {ContentKey}"]);
    }

    [Fact]
    public async Task A_retry_carrying_a_different_command_is_refused_and_the_handler_does_not_run()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result<string>> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success("order-1"));
        }

        await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            Handler,
            TestContext.Current.CancellationToken);

        IdempotencyEntry recorded = store.Entries[ContentKey];

        CommandIdReusedException thrown = await Should.ThrowAsync<CommandIdReusedException>(
            () => Content(store).HandleAsync(
                new ContentCommand(Command, "three desks"),
                Handler,
                TestContext.Current.CancellationToken));

        thrown.CommandId.ShouldBe(Command);
        handlerRuns.ShouldBe(1, "a 200 carrying the first request's result would say the second was applied");
        store.Entries[ContentKey].ShouldBe(recorded, "the refusal leaves the first request's entry as it found it");
        store.Calls[^2..].ShouldBe([$"claim {ContentKey}", $"get {ContentKey}"], "neither a release nor a write");
    }

    [Fact]
    public async Task An_entry_the_previous_release_wrote_replays_with_no_fingerprint_to_compare()
    {
        // The shape before ADR-057: the bare value, which a rolling deploy leaves live for the claim's window.
        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, "\"order-1\"");

        Result<string> result = await Content(store).HandleAsync(
            new ContentCommand(Command, "whatever the first request carried"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe("order-1");
    }

    [Fact]
    public async Task A_previous_release_s_string_that_spells_the_prefix_is_still_a_bare_value()
    {
        // A JSON string opens with a quote, so the prefix is matched at the payload's first character only.
        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, "\"sha256:not-a-fingerprint\"");

        Result<string> result = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe("sha256:not-a-fingerprint");
    }

    [Fact]
    public async Task A_result_whose_JSON_holds_a_colon_survives_the_envelope()
    {
        // The envelope is stripped by its length, never by splitting on the separator.
        RecordingIdempotencyStore store = new();

        await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => Task.FromResult(Result.Success("urn:order:1")),
            TestContext.Current.CancellationToken);

        Result<string> replayed = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        replayed.Value.ShouldBe("urn:order:1");
    }

    [Fact]
    public async Task A_void_command_is_stored_behind_its_fingerprint_and_replays()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;

        Task<Result> Handler()
        {
            handlerRuns++;
            return Task.FromResult(Result.Success());
        }

        await Void(store).HandleAsync(new VoidProtectedCommand(Command), Handler, TestContext.Current.CancellationToken);

        Result replayed = await Void(store).HandleAsync(
            new VoidProtectedCommand(Command),
            Handler,
            TestContext.Current.CancellationToken);

        store.Entries[VoidKey].Payload.ShouldBe($"sha256:{FingerprintOf(BareJson)}:null");
        replayed.IsSuccess.ShouldBeTrue();
        handlerRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_void_entry_under_another_fingerprint_is_refused_though_it_has_no_value_to_read()
    {
        // The comparison runs before the no-value shortcut, or every void command would replay any request.
        RecordingIdempotencyStore store = new();
        store.Completed(VoidKey, $"sha256:{new string('0', 64)}:null");

        await Should.ThrowAsync<CommandIdReusedException>(
            () => Void(store).HandleAsync(
                new VoidProtectedCommand(Command),
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stored_fingerprint_that_is_this_commands_exactly_replays()
    {
        // Pins the spelled-out JSON to CommandFingerprint.Of, without which a refused envelope proves nothing.
        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, $"sha256:{FingerprintOf(TwoDesksJson)}:\"order-1\"");

        Result<string> result = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => throw new InvalidOperationException("the handler must not run on a replay"),
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe("order-1");
    }

    [Theory]
    [InlineData(nameof(Stored.UpperCase))]
    [InlineData(nameof(Stored.Truncated))]
    [InlineData(nameof(Stored.Unseparated))]
    public async Task A_stored_fingerprint_that_is_not_this_commands_exactly_is_refused(string stored)
    {
        // Ordinal, and fail-closed: an envelope this behaviour did not write is never read as a match.
        ContentCommand command = new(Command, "two desks");
        string fingerprint = FingerprintOf(TwoDesksJson);

        string payload = stored switch
        {
            nameof(Stored.UpperCase) => $"sha256:{fingerprint.ToUpperInvariant()}:\"order-1\"",
            nameof(Stored.Truncated) => $"sha256:{fingerprint[..32]}:\"order-1\"",
            _ => $"sha256:{fingerprint}\"order-1\""
        };

        RecordingIdempotencyStore store = new();
        store.Completed(ContentKey, payload);

        await Should.ThrowAsync<CommandIdReusedException>(
            () => Content(store).HandleAsync(
                command,
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    private enum Stored
    {
        UpperCase,
        Truncated,
        Unseparated
    }

    [Fact]
    public async Task An_in_flight_duplicate_carrying_a_different_command_is_still_told_to_retry()
    {
        // The fingerprint is recorded with the outcome, so there is nothing to compare until one exists (ADR-057).
        RecordingIdempotencyStore store = new();
        store.InFlight(ContentKey);

        await Should.ThrowAsync<ConcurrentRequestException>(
            () => Content(store).HandleAsync(
                new ContentCommand(Command, "three desks"),
                () => throw new InvalidOperationException("the handler must not run under a held key"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_command_that_cannot_be_fingerprinted_claims_nothing()
    {
        // System.Text.Json refuses a System.Type; thrown after the claim, that would hold the key for the window.
        RecordingIdempotencyStore store = new();

        IdempotencyBehavior<UnserialisableCommand, Result> behaviour =
            new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

        await Should.ThrowAsync<NotSupportedException>(
            () => behaviour.HandleAsync(
                new UnserialisableCommand(Command, typeof(string)),
                () => Task.FromResult(Result.Success()),
                TestContext.Current.CancellationToken));

        store.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_command_stores_nothing_so_its_id_may_carry_a_corrected_request()
    {
        RecordingIdempotencyStore store = new();

        await Content(store).HandleAsync(
            new ContentCommand(Command, "no desks"),
            () => Task.FromResult(Result.Failure<string>(Error.Rule("test.refused", "No."))),
            TestContext.Current.CancellationToken);

        Result<string> corrected = await Content(store).HandleAsync(
            new ContentCommand(Command, "two desks"),
            () => Task.FromResult(Result.Success("order-1")),
            TestContext.Current.CancellationToken);

        corrected.Value.ShouldBe("order-1");
    }

    private static IdempotencyBehavior<ContentCommand, Result<string>> Content(RecordingIdempotencyStore store) =>
        new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

    private static IdempotencyBehavior<VoidProtectedCommand, Result> Void(RecordingIdempotencyStore store) =>
        new(store, StubCurrentUser.Authenticated(Caller), new IdempotencyContext());

    /// <summary>A command carrying its <c>CommandId</c> and nothing else, as the fingerprint serialises it.</summary>
    private static string BareJson => $$"""{"CommandId":"{{Command}}"}""";

    /// <summary>The <c>ContentCommand</c> carrying "two desks", as the fingerprint serialises it.</summary>
    private static string TwoDesksJson => $$"""{"CommandId":"{{Command}}","Content":"two desks"}""";

    /// <summary>ADR-057's fingerprint of a command whose JSON the test spells out, so the hashed shape is pinned.</summary>
    private static string FingerprintOf(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static string ExpectedKey => $"{Caller}:{ProtectedCommand.OperationName}:{Command}";

    private static string ContentKey => $"{Caller}:{ContentCommand.OperationName}:{Command}";

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

/// <summary>An opted-in command with content, so two requests can share a <c>CommandId</c> and differ.</summary>
public sealed record ContentCommand(Guid CommandId, string Content) : ICommand<Result<string>>, IIdempotentCommand
{
    public static string OperationName => "tests.content";
}

/// <summary>An opted-in command <c>System.Text.Json</c> refuses to serialise.</summary>
public sealed record UnserialisableCommand(Guid CommandId, Type Shape) : ICommand<Result>, IIdempotentCommand
{
    public static string OperationName => "tests.unserialisable";
}

/// <summary>Satisfies the behaviour's result constraint but not <see cref="IIdempotentCommand"/>.</summary>
public sealed record UnprotectedCommand : ICommand<Result>;

public sealed class UnprotectedCommandHandler : ICommandHandler<UnprotectedCommand, Result>
{
    public Task<Result> HandleAsync(UnprotectedCommand command, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}
