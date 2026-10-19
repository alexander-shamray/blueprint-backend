using Common.Infrastructure.Outbox;

namespace Common.TestSupport;

/// <summary>The outbox dispatcher's failure backoff, read off a row it has just failed.</summary>
/// <remarks>
/// The claim leases a row for <see cref="OutboxDispatcher.LeaseSeconds"/>, so a row that merely has a
/// <c>LockedUntil</c> shows the lease and not the backoff; this reads how far off the instant is. It answers with
/// the reason rather than asserting, since this project carries no assertion library.
/// </remarks>
public static class OutboxBackoffRule
{
    /// <summary>The time between the failure and the read, and the database's clock against this host's.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(8);

    /// <summary>The ladder's wait for a failure made at <paramref name="attemptsBefore"/> attempts.</summary>
    public static TimeSpan Ladder(int attemptsBefore) =>
        TimeSpan.FromSeconds(
            Math.Pow(2, Math.Min(attemptsBefore, OutboxDispatcher.BackoffAttemptCap)) *
            OutboxDispatcher.BackoffBaseSeconds);

    /// <summary>Null when the row's <c>LockedUntil</c> is the ladder's wait from now, within the slack.</summary>
    public static string? Violation(OutboxMessage row, int attemptsBefore)
    {
        TimeSpan expected = Ladder(attemptsBefore);

        if (row.LockedUntil is not { } lockedUntil)
            return "a failed row is backed off, not left with no LockedUntil";

        TimeSpan remaining = lockedUntil - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
            return $"no backoff time is left ({remaining.TotalSeconds:F1}s), so the next pass claims the row at once";

        if (remaining < expected - Slack || remaining > expected + TimeSpan.FromSeconds(2))
        {
            return $"{remaining.TotalSeconds:F1}s remain where the ladder waits {expected.TotalSeconds:F0}s and the " +
                $"claim's own lease is {OutboxDispatcher.LeaseSeconds}s";
        }

        return null;
    }
}
