namespace Common.Application;

/// <summary>§8.5's Redis claim store, as a port, because §4.2 keeps Redis out of this assembly.</summary>
/// <remarks>Takes <c>{subject}:{operation}:{commandId}</c>; the implementation owns the prefix (§8.3).</remarks>
public interface IIdempotencyStore
{
    /// <summary>Atomically claims the key; returns the claim token, or null if the key is held.</summary>
    Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct);

    /// <summary>The entry behind a key, or null; not token-checked, since its caller holds no claim.</summary>
    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct);

    /// <summary>Records the outcome if this claim still owns the key, keeping its remaining life.</summary>
    /// <remarks>No retention parameter: the window runs from the claim, not the commit (ADR-038).</remarks>
    Task CompleteAsync(string key, string claim, string payload, CancellationToken ct);

    /// <summary>Frees a key this claim still owns; best-effort, as it is called from a <c>catch</c>.</summary>
    Task ReleaseAsync(string key, string claim, CancellationToken ct);

    /// <summary>Which of <paramref name="keys"/> this store no longer holds, for the marker's purge.</summary>
    /// <remarks>Answers for every key or throws: a key wrongly reported unheld loses its marker (ADR-039).</remarks>
    Task<IReadOnlyCollection<string>> UnheldAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken ct);
}

/// <summary>A claimed key's contents; <paramref name="Payload"/> is null while <paramref name="InProgress"/>.</summary>
public sealed record IdempotencyEntry(bool InProgress, string? Payload);
