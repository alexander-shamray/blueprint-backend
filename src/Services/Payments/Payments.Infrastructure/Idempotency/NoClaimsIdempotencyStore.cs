using Common.Application;

namespace Payments.Infrastructure.Idempotency;

/// <summary>
/// §2: this service opts no command into idempotency, so nothing here ever
/// claims a key. <c>RetentionPurgeService</c> (Common.Infrastructure) still
/// resolves <see cref="IIdempotencyStore"/> unconditionally to purge the
/// marker table by age (ADR-039); every other member exists only to say why
/// a caller reached it in error.
/// </summary>
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

    // No claim is ever taken, so none can still be held: every key the purge
    // asks about is unheld (ADR-039).
    public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult(keys);

    private static InvalidOperationException NoIdempotentCommand() =>
        new("Payments has no IIdempotentCommand (§8.5); giving it one means registering the " +
            "Redis-backed IIdempotencyStore, not this one.");
}
