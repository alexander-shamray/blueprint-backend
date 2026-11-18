using Common.Infrastructure.Outbox;

namespace Common.TestSupport;

/// <summary>The outbox dispatcher's failure backoff, read off a row it has just failed.</summary>
/// <remarks>
/// The claim's <see cref="OutboxDispatcher.LeaseSeconds"/> lease also sets a <c>LockedUntil</c>, so this reads how far
/// off it is. It answers with the reason, not an assertion: this project carries no assertion library.
/// </remarks>
public static class OutboxBackoffRule
{
    /// <summary>The time between the failure and the read, which only shortens what remains.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(8);

    /// <summary>Above the wait, a rounding margin only: the stamp and the read are both the database's clock, and
    /// the read comes after, so nothing else lengthens what remains.</summary>
    private static readonly TimeSpan Rounding = TimeSpan.FromSeconds(1);

    /// <summary>The ladder's wait for a failure made at <paramref name="attemptsBefore"/> attempts.</summary>
    public static TimeSpan Ladder(int attemptsBefore) =>
        TimeSpan.FromSeconds(
            Math.Pow(2, Math.Min(attemptsBefore, OutboxDispatcher.BackoffAttemptCap)) *
            OutboxDispatcher.BackoffBaseSeconds);

    /// <summary>Null when the row's <c>LockedUntil</c> is the ladder's wait from <paramref name="now"/>, the
    /// database's clock that stamped it.</summary>
    public static string? Violation(OutboxMessage row, int attemptsBefore, DateTimeOffset now) =>
        Violation(row.LockedUntil, attemptsBefore, now);

    /// <summary>Null when <paramref name="lockedUntil"/> is the ladder's wait from <paramref name="now"/>.</summary>
    public static string? Violation(DateTimeOffset? lockedUntil, int attemptsBefore, DateTimeOffset now)
    {
        TimeSpan expected = Ladder(attemptsBefore);

        if (lockedUntil is not { } until)
            return "a failed row is backed off, not left with no LockedUntil";

        TimeSpan remaining = until - now;

        if (remaining <= TimeSpan.Zero)
            return $"no backoff time is left ({remaining.TotalSeconds:F1}s), so the next pass claims the row at once";

        if (remaining < expected - Slack || remaining > expected + Rounding)
        {
            return $"{remaining.TotalSeconds:F1}s remain where the ladder waits {expected.TotalSeconds:F0}s and the " +
                $"claim's own lease is {OutboxDispatcher.LeaseSeconds}s";
        }

        return null;
    }
}
