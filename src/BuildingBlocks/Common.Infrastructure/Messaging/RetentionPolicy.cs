using System.Runtime.CompilerServices;
using Common.Application;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// How long processed outbox rows, handled inbox rows and §8.5's committed
/// idempotency markers are kept, and how the purge that deletes them is paced
/// (§9.4, §9.5, §8.5). Two of those windows are housekeeping and the third is
/// a correctness setting — see <see cref="IdempotencyWindow"/>, the only one
/// with a floor. Registered rather than a <c>const</c>, for the reason
/// <see cref="Outbox.OutboxTable"/> is: the numbers are a service's to choose,
/// and a constant in common code is a choice made once for everybody.
/// </summary>
/// <remarks>
/// Every member is refused rather than clamped, because each fails somewhere
/// the reader is not looking: a negative window puts the cutoff in the future
/// and deletes rows written a second ago; a non-positive count makes every
/// pass a no-op with the tables growing; and a value past the upper bounds
/// throws on a background thread or inside a swallowed pass rather than at
/// the registration that set it.
/// </remarks>
public sealed record RetentionPolicy
{
    private readonly TimeSpan _outboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _inboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _idempotencyWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly int _batchSize = 5000;
    private readonly int _maxBatchesPerPass = 20;

    /// <summary>
    /// Processed outbox rows older than this are deleted. A soft window,
    /// because processed rows are kept for debugging (§9.4); the predicate is
    /// <see cref="RetentionPurgeService"/>'s.
    /// </summary>
    public TimeSpan OutboxWindow
    {
        get => _outboxWindow;
        init => _outboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>
    /// Inbox rows handled longer ago than this are deleted. A constraint rather
    /// than a round number: §9.5 requires it to exceed the broker's longest
    /// redelivery delay, error queue included, because pruning sooner lets a
    /// late redelivery through as new.
    /// </summary>
    public TimeSpan InboxWindow
    {
        get => _inboxWindow;
        init => _inboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>
    /// Idempotency markers committed longer ago than this are deleted, and
    /// this window is §8.5's guarantee rather than a housekeeping setting.
    /// </summary>
    /// <remarks>
    /// The floor is <see cref="IdempotencyRetention.MarkerFloor"/>, the
    /// claim's own window, read rather than restated so the two cannot
    /// disagree; equal is admitted because the claim precedes the marker
    /// (ADR-038). What this chooses is the length of §8.5's guarantee, not
    /// its truth: <see cref="RetentionPurgeService"/> deletes a marker only
    /// once its claim is gone (ADR-039), so a marker outlives its claim
    /// whatever this says.
    /// </remarks>
    public TimeSpan IdempotencyWindow
    {
        get => _idempotencyWindow;
        init => _idempotencyWindow = AtLeast(InRange(value, MaxWindow), IdempotencyRetention.MarkerFloor);
    }

    /// <summary>
    /// Rows per statement. §9.5 asks for the purge to be batched so neither
    /// holds a long lock, and 5000 is the figure §9.4's and §9.5's arithmetic
    /// about the dispatcher's rate is written against — see
    /// <see cref="MaxBatchesPerPass"/>.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        init => _batchSize = Positive(value);
    }

    /// <summary>
    /// How often a pass runs. Slow on purpose: retention is a housekeeping
    /// concern measured in days, and a purge competing with the dispatcher's
    /// twice-a-second claim for the same table's locks buys nothing.
    /// </summary>
    public TimeSpan Interval
    {
        get => _interval;
        init => _interval = InRange(value, MaxInterval);
    }

    /// <summary>
    /// Batches per table per pass. A ceiling rather than a target: without one,
    /// a first run against a table that was never purged loops until it is
    /// empty, holding a connection and competing with the dispatcher for as
    /// long as that takes. With it the backlog drains over several passes and
    /// each pass is bounded.
    /// </summary>
    /// <remarks>
    /// The bound is below the dispatcher's, which is the opposite of what it
    /// looks like: twenty batches of 5,000 an hour is about 28 rows a second,
    /// where <c>OutboxDispatcher</c> claims up to 100 rows twice a second. That
    /// is not a competition at ordinary load, because a row is only purgeable
    /// once <see cref="OutboxWindow"/> has passed and that much backlog is
    /// what the window is for; at sustained peak the answer is a
    /// shorter <see cref="Interval"/> or a larger ceiling, and §13.6's
    /// outbox-growth alert is what makes the need visible.
    /// </remarks>
    public int MaxBatchesPerPass
    {
        get => _maxBatchesPerPass;
        init => _maxBatchesPerPass = Positive(value);
    }

    /// <summary>
    /// <c>PeriodicTimer</c>'s largest accepted period, which is where
    /// <see cref="Interval"/> is actually spent.
    /// </summary>
    /// <remarks>
    /// <c>uint.MaxValue</c> milliseconds throws and one less constructs — about
    /// 49.7 days either way. Anything larger is refused here rather than by the
    /// constructor in <c>ExecuteAsync</c>, where it would throw on a background
    /// thread inside a host that had already reported ready.
    /// </remarks>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    /// <remarks>
    /// A bound is needed because the outbox's and the inbox's cutoffs are
    /// <c>now - window</c>, and <c>DateTimeOffset</c> subtraction throws when
    /// the result is not representable — inside <c>PurgeAsync</c>, whose caller
    /// logs and swallows, so an unbounded window buys a purge that never runs.
    /// The marker's cutoff is computed in SQL, and ten years clears that
    /// ceiling too: 315,360,000 seconds fits <c>DATEADD</c>'s <c>int</c>. Ten
    /// years rather than the representable maximum because past a decade the
    /// value is a mistake, worth refusing at the registration.
    /// </remarks>
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
