namespace Shipping.Infrastructure.Carrier;

/// <summary>The carrier call's budget and the workers' intervals, sized to a worker's leased row (§9.7).</summary>
/// <remarks>Retries are safe as each write carries an idempotency key; public as <c>Program</c> is (§4.2).</remarks>
public static class CarrierHop
{
    /// <summary>Above §9.7's band, as a third party's is: nothing waits on this hop, and the row backs off.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Strictly below <c>ServiceOptions.OperationTimeout</c> (§9.7), and each lease sits above it.</summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to a worker's call rate: a loop never reaches the endpoint default's hundred.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    /// <summary>At least twice <see cref="AttemptTimeout"/>, which the library validates at startup.</summary>
    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>A page of events is a few short fields each, so a larger answer is refused unread.</summary>
    public const int MaxAnswerBytes = 128 * 1024;

    /// <summary>The most events one page may hold before it is refused whole.</summary>
    public const int MaxEventsPerPage = 200;

    /// <summary>How far ahead of this host's clock a carrier's timestamp may sit before its page is refused.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Paced by new orders.</summary>
    public static readonly TimeSpan FulfilmentTick = TimeSpan.FromSeconds(5);

    /// <summary>Equal to <see cref="FulfilmentTick"/> without being it: the loops are paced apart.</summary>
    public static readonly TimeSpan TrackingTick = TimeSpan.FromSeconds(5);

    /// <summary>Adds to §13.7's event target, since nothing learns of a despatch before the next poll.</summary>
    public static readonly TimeSpan TrackingPollInterval = TimeSpan.FromSeconds(30);
}
