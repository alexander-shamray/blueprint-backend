using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>ADR-057's fingerprint: what makes two requests under one <c>CommandId</c> the same request.</summary>
/// <remarks>Read off the stored payload, as a retry meets it: <c>CommandFingerprint</c> is internal (§8.5).</remarks>
public class CommandFingerprintTests
{
    private static readonly Guid Command = Guid.Parse("0195e4b2-0000-7000-8000-0000000000ff");
    private static readonly Guid Desk = Guid.Parse("0195e4b2-0000-7000-8000-0000000000d1");
    private static readonly Guid Lamp = Guid.Parse("0195e4b2-0000-7000-8000-0000000000d2");

    private sealed record Bare(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.bare";
    }

    private sealed record Line(Guid ProductId, int Quantity);

    private sealed record Basket(Guid CommandId, IReadOnlyList<Line> Lines, decimal Total, string Currency)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.basket";
    }

    /// <summary><see cref="Basket"/> one release later, with an optional field appended.</summary>
    private sealed record BasketWithNote(
        Guid CommandId,
        IReadOnlyList<Line> Lines,
        decimal Total,
        string Currency,
        string? Note = null) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "tests.fingerprint.basket";
    }

    private static Basket TwoDesksAndALamp() => new(Command, [new Line(Desk, 2), new Line(Lamp, 1)], 149.90m, "EUR");

    [Fact]
    public async Task The_fingerprint_is_the_SHA_256_of_the_commands_JSON_in_lower_case_hex()
    {
        // The JSON is spelled out, so a naming policy or a GUID format added to the options fails here.
        string json = $$"""{"CommandId":"{{Command}}"}""";
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        string fingerprint = await FingerprintOfAsync(new Bare(Command));

        fingerprint.ShouldBe(expected);
        fingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Equal_commands_hash_equal_though_they_are_two_instances()
    {
        // The two are not Equals-equal, since a record compares a list by reference; their content is.
        (await FingerprintOfAsync(TwoDesksAndALamp())).ShouldBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task One_changed_field_hashes_different()
    {
        string original = await FingerprintOfAsync(TwoDesksAndALamp());

        (await FingerprintOfAsync(TwoDesksAndALamp() with { Currency = "USD" })).ShouldNotBe(original);
        (await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 149.91m })).ShouldNotBe(original);
        (await FingerprintOfAsync(TwoDesksAndALamp() with { CommandId = Guid.CreateVersion7() }))
            .ShouldNotBe(original);
    }

    [Fact]
    public async Task A_change_inside_a_nested_line_hashes_different()
    {
        Basket threeDesks = TwoDesksAndALamp() with { Lines = [new Line(Desk, 3), new Line(Lamp, 1)] };

        (await FingerprintOfAsync(threeDesks)).ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task A_reordered_list_is_a_different_request()
    {
        // ADR-057's residual, pinned: the fingerprint is of the bound command, and a list binds in order.
        Basket reordered = TwoDesksAndALamp() with { Lines = [new Line(Lamp, 1), new Line(Desk, 2)] };

        (await FingerprintOfAsync(reordered)).ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp()));
    }

    [Fact]
    public async Task A_decimal_keeps_the_scale_it_was_sent_with()
    {
        // The same residual: 10 and 10.0 bind to two decimals, so a retry re-sends the bytes it sent.
        (await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 10m }))
            .ShouldNotBe(await FingerprintOfAsync(TwoDesksAndALamp() with { Total = 10.0m }));
    }

    [Fact]
    public async Task A_defaulted_optional_field_hashes_as_its_absence_does()
    {
        // What lets a retry straddle the deploy that added the field (ADR-057).
        Basket before = TwoDesksAndALamp();
        BasketWithNote after = new(before.CommandId, before.Lines, before.Total, before.Currency);

        (await FingerprintOfAsync(after)).ShouldBe(await FingerprintOfAsync(before));
        (await FingerprintOfAsync(after with { Note = "Leave with the porter." }))
            .ShouldNotBe(await FingerprintOfAsync(before));
    }

    /// <summary>The fingerprint a retry is compared with: the one stored beside the first attempt's result.</summary>
    private static async Task<string> FingerprintOfAsync<TCommand>(TCommand command)
        where TCommand : ICommand<Result>, IIdempotentCommand
    {
        const string prefix = "sha256:";
        const string noValue = ":null";

        RecordingIdempotencyStore store = new();
        IdempotencyBehavior<TCommand, Result> behaviour =
            new(store, StubCurrentUser.Anonymous(), new IdempotencyContext());

        await behaviour.HandleAsync(
            command,
            () => Task.FromResult(Result.Success()),
            TestContext.Current.CancellationToken);

        string payload = store.Entries.Values.ShouldHaveSingleItem().Payload.ShouldNotBeNull();
        payload.ShouldStartWith(prefix, Case.Sensitive);
        payload.ShouldEndWith(noValue, Case.Sensitive);

        return payload[prefix.Length..^noValue.Length];
    }
}
