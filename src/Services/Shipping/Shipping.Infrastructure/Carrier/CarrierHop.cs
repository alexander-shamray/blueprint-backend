namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The carrier call's budget and the intervals the two workers run at, in one
/// class because section 4's arithmetic spans them: the total sits under
/// <c>ServiceOptions.OperationTimeout</c>, each lease above the total.
/// </summary>
/// <remarks>
/// Public for the reason <c>Program</c> is (§4.2): one modifier commits less
/// than an <c>InternalsVisibleTo</c>. Retrying inside the budget is safe only
/// because a call that writes carries its idempotency key (spec, section 4).
/// </remarks>
public static class CarrierHop
{
    /// <summary>
    /// Deliberately above §9.7's one-to-two-second band, for that section's
    /// own reason: a third party is sized to the wait above it, and nothing
    /// waits on this hop — the row backs off.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The cap on one jittered delay. With jitter on, <see cref="RetryDelay"/>
    /// is a nominal and not a bound; this is what makes the budget arithmetic.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Strictly below <c>ServiceOptions.OperationTimeout</c> (§9.7), and
    /// inside the thirty-second drain a worker's pass has to fit.
    /// </summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// Sized to a worker's call rate, which is what the endpoint default is
    /// not: a hundred calls in a sampling window is a threshold a loop ticking
    /// every few seconds never reaches, so the breaker would never open. For
    /// the fulfilment pass, four attempts is two failed calls and the third is
    /// refused; a tracking pass sends its batch at once, so there the breaker
    /// bounds an outage to one <c>TrackingWorker.ClaimBatchSize</c> per replica.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    /// <summary>
    /// At least twice <see cref="AttemptTimeout"/>, which the library
    /// validates at startup: a window a single attempt can outlast cannot
    /// sample it.
    /// </summary>
    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>How long an open circuit refuses before one probe is let through.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most an answer may carry. A page of events is a few short fields
    /// each, so a larger body is a carrier this adapter does not understand.
    /// </summary>
    public const int MaxAnswerBytes = 128 * 1024;

    /// <summary>The most events one page may hold before it is refused whole.</summary>
    public const int MaxEventsPerPage = 200;

    /// <summary>
    /// How far ahead of this host's clock a carrier's timestamp may sit. A
    /// stored future instant sits ahead of every real one for ever, which is
    /// a promotion that never completes.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the fulfilment loop claims a batch. Paced by new orders.
    /// </summary>
    public static readonly TimeSpan FulfilmentTick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the tracking loop claims the rows that are due. Short because
    /// due-ness lives on the row as <c>NextPollAt</c>, so the tick only bounds
    /// how late a due row is picked up; it matches <see cref="FulfilmentTick"/>
    /// without being it, since the two loops are paced by different things.
    /// </summary>
    public static readonly TimeSpan TrackingTick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after an applied page a shipment is next due a poll, stamped on
    /// the row. A latency number before it is a load number: nothing downstream
    /// learns of a despatch sooner than the next poll, so this and one
    /// <see cref="TrackingTick"/> are added to §13.7's two-second event target,
    /// and it is what keeps a busy table inside a carrier's rate limit.
    /// </summary>
    public static readonly TimeSpan TrackingPollInterval = TimeSpan.FromSeconds(30);
}
