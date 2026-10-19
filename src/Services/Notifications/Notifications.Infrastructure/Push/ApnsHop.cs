namespace Notifications.Infrastructure.Push;

/// <summary>The APNs call's budget, sized to a leased delivery row and outside §9.7's bands by rule 1.</summary>
/// <remarks>
/// <c>MailHop</c>'s numbers to start from, as the design has it, owned apart from <see cref="FcmHop"/> because each
/// provider fails alone. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public static class ApnsHop
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

    /// <summary>As <see cref="FcmHop.CircuitBreakerMinimumThroughput"/>, for the same fan-out.</summary>
    public const int CircuitBreakerMinimumThroughput = 11;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>How long a provider token is used, inside Apple's twenty to sixty minutes.</summary>
    public static readonly TimeSpan ProviderTokenLifetime = TimeSpan.FromMinutes(40);

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
