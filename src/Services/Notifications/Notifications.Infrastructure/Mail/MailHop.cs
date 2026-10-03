namespace Notifications.Infrastructure.Mail;

/// <summary>The relay call's budget and the send worker's tick, sized to a worker's leased row (§9.7).</summary>
/// <remarks>
/// A retry repeats only an attempt the relay never took the message in, §9.7's fourth rule for a send that
/// carries no idempotency key. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public static class MailHop
{
    /// <summary>Above §9.7's band, as a third party's is: nothing waits on this hop, and the row backs off.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more connection than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Strictly below <c>ServiceOptions.OperationTimeout</c> (§9.7); the worker's lease is above.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to a worker's call rate: two failed sends open it, where a hundred never would.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>Shorter than the window, so the breaker keeps its failures while open.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>How often the send worker claims, paced by new events rather than by the relay.</summary>
    public static readonly TimeSpan SendTick = TimeSpan.FromSeconds(5);
}
