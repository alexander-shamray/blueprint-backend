namespace Payments.Infrastructure.Provider;

/// <summary>The provider call's budget, sized to fit the saga's payment wait (§9.7, §9.6).</summary>
/// <remarks>Retries are safe as each request carries an idempotency key; public as <c>Program</c> is (§4.2).</remarks>
public static class ProviderHop
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Every defined answer is a few short fields, so a larger one is refused unread.</summary>
    public const int MaxAnswerBytes = 64 * 1024;
}
