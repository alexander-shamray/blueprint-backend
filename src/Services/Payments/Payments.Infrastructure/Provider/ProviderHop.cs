namespace Payments.Infrastructure.Provider;

/// <summary>
/// The provider call's budget. Every retry inside it is safe only because each
/// request carries its idempotency key (spec, section 4); the endpoint's policy
/// (§9.8) owns every retry after it, and the worst case of both stays inside
/// the saga's payment wait (§9.6).
/// </summary>
/// <remarks>
/// Public for the reason <c>Program</c> is (§4.2): read from another assembly,
/// and one modifier commits less than an <c>InternalsVisibleTo</c>.
/// </remarks>
public static class ProviderHop
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The cap on one jittered delay. With jitter on, <see cref="RetryDelay"/> is
    /// a nominal and not a bound; this is what makes the budget arithmetic.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The most an answer may carry. Every answer the wire format defines is a
    /// few short fields, so a larger one is a provider this adapter does not
    /// understand, refused before it is read.
    /// </summary>
    public const int MaxAnswerBytes = 64 * 1024;
}
