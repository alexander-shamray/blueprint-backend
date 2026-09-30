using System.Runtime.CompilerServices;
using Common.Application;

namespace Common.Infrastructure.Messaging;

/// <summary>The retention windows of §9.4, §9.5 and §8.5, and the pacing of the purge that applies them.</summary>
public sealed record RetentionPolicy
{
    private readonly TimeSpan _outboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _inboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _idempotencyWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly int _batchSize = 5000;
    private readonly int _maxBatchesPerPass = 20;

    /// <summary>A soft window, because processed rows are kept only for debugging (§9.4).</summary>
    public TimeSpan OutboxWindow
    {
        get => _outboxWindow;
        init => _outboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>Must exceed the broker's longest redelivery delay, error queue included (§9.5).</summary>
    public TimeSpan InboxWindow
    {
        get => _inboxWindow;
        init => _inboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>§8.5's guarantee rather than housekeeping, the one window with a floor (§9.5).</summary>
    /// <remarks>
    /// Floored at <see cref="IdempotencyRetention.MarkerFloor"/>, equal allowed as the claim precedes the marker
    /// (ADR-038); above it the window is a target, since the purge waits for the claim to go (ADR-039).
    /// </remarks>
    public TimeSpan IdempotencyWindow
    {
        get => _idempotencyWindow;
        init => _idempotencyWindow = AtLeast(InRange(value, MaxWindow), IdempotencyRetention.MarkerFloor);
    }

    /// <summary>Rows per statement, batched so no purge holds a long lock (§9.5).</summary>
    public int BatchSize
    {
        get => _batchSize;
        init => _batchSize = Positive(value);
    }

    /// <summary>Slow on purpose: retention is in days, and the dispatcher claims from the same table.</summary>
    public TimeSpan Interval
    {
        get => _interval;
        init => _interval = InRange(value, MaxInterval);
    }

    /// <summary>A ceiling per table per pass, so a backlog drains over several bounded passes.</summary>
    public int MaxBatchesPerPass
    {
        get => _maxBatchesPerPass;
        init => _maxBatchesPerPass = Positive(value);
    }

    /// <summary><c>PeriodicTimer</c>'s largest period, refused here rather than on a background thread.</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Ten years: past that a configuration error, and within <c>DATEADD</c>'s <c>int</c> seconds.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(3650);

    private static TimeSpan InRange(
        TimeSpan value,
        TimeSpan maximum,
        [CallerMemberName] string member = "") =>
        value > TimeSpan.Zero && value <= maximum ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive and at most {maximum}. A retention setting outside " +
                "that range does not fail where it is set — it deletes rows that were just " +
                "written, purges nothing at all, or throws where the exception is swallowed.");

    private static TimeSpan AtLeast(
        TimeSpan value,
        TimeSpan floor,
        [CallerMemberName] string member = "") =>
        value >= floor ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be at least {floor} — how long §8.5's Redis claim survives — " +
                "because a shorter window asks for a guarantee shorter than the claim already " +
                "gives, which is a setting that cannot do what it says. The purge deletes a " +
                "marker only once IIdempotencyStore reports the claim behind its key gone " +
                "(ADR-039), so a window below the claim's does not shorten anything: the row " +
                "still survives until the claim expires, and the number written here would be " +
                "one nothing acts on. It used to be refused for a stronger reason — a shorter " +
                "window purged the marker first, the claim then expired with nothing left to " +
                "remember the commit, and the next retry ran a committed command a second " +
                "time. That is what ADR-039 closed. What this floor bounds now is how long " +
                "the guarantee lasts rather than whether it holds, and matching the claim " +
                "exactly is the smallest window that means anything. Setting it higher is a " +
                "TARGET rather than a guaranteed extension: the candidate half is still an age " +
                "against the database's clock, so a forward step of that clock makes the row " +
                "eligible early and it is then deleted as soon as the claim goes. The claim's " +
                "own life is the floor under the guarantee in every case; see " +
                "IdempotencyRetention.MarkerFloor.");

    private static int Positive(int value, [CallerMemberName] string member = "") =>
        value > 0 ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive. A non-positive count does not fail where it is " +
                "set — it makes every pass a no-op, so retention stops with the tables growing " +
                "and nothing to see.");
}
