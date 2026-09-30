using Common.Application;

namespace Shipping.Infrastructure.Idempotency;

/// <summary>Shipping has no IIdempotentCommand, so nothing claims a key; the purge calls this (ADR-039).</summary>
internal sealed class NoClaimsIdempotencyStore : IIdempotencyStore
{
    public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
        throw NoIdempotentCommand();

    // No claim is ever taken, so every key the purge asks about is unheld (ADR-039).
    public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult(keys);

    private static InvalidOperationException NoIdempotentCommand() =>
        new("Shipping has no IIdempotentCommand (§8.5); giving it one means registering the " +
            "Redis-backed IIdempotencyStore, not this one.");
}
