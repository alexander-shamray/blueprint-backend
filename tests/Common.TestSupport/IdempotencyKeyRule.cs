using Common.Infrastructure.Idempotency;

namespace Common.TestSupport;

/// <summary>§8.5's marker key, <c>{subject}:{operation}:{commandId}</c>, against the column that holds it.</summary>
public static class IdempotencyKeyRule
{
    /// <summary>The column less a GUID subject, a GUID CommandId and the two separators, the widest those run.</summary>
    public static readonly int LongestOperationName =
        IdempotencyMarker.KeyMaxLength - ((2 * Guid.Empty.ToString().Length) + 2);
}
