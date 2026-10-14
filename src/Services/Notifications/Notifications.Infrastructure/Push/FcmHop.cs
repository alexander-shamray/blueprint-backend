namespace Notifications.Infrastructure.Push;

/// <summary>The FCM call's budget, sized to a leased delivery row and outside §9.7's bands by rule 1.</summary>
/// <remarks>
/// <c>MailHop</c>'s numbers to start from, as the design has it. A retry repeats only an attempt FCM took nothing in,
/// §9.7's fourth rule for a send with no idempotency key. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public static class FcmHop
{
    /// <summary>Above §9.7's band, as a third party's is: nothing waits on this hop, and the row backs off.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// Sized to §9.7 rule 2's call rate: one event fans out to as many devices as a subject keeps, so a window must
    /// hold more than one subject's deliveries before its ratio speaks for the provider rather than for them.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 10;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    internal static PushHopBudget Budget { get; } = new(
        AttemptTimeout,
        MaxRetryAttempts,
        RetryDelay,
        MaxRetryDelay,
        TotalTimeout,
        CircuitBreakerFailureRatio,
        CircuitBreakerMinimumThroughput,
        CircuitBreakerSamplingDuration,
        CircuitBreakerBreakDuration);
}
