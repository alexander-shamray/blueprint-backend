namespace Common.Infrastructure.Idempotency;

/// <summary>§8.5's durable half: one command, under one scoped idempotency key, whose work committed.</summary>
/// <remarks>
/// <c>CommittedAt</c> is stamped by the database and only selects purge candidates (ADR-038, ADR-039).
/// The <see cref="RowVersionColumn"/> shadow property identifies a row to the purge's delete (ADR-041).
/// </remarks>
public sealed class IdempotencyMarker(string key, DateTimeOffset committedAt = default)
{
    /// <summary>Named once, so each service's mapping and <c>RetentionPurgeService</c>'s SQL agree.</summary>
    public const string RowVersionColumn = "RowVersion";

    public string Key { get; private set; } = key;

    public DateTimeOffset CommittedAt { get; private set; } = committedAt;
}
