namespace Notifications.Infrastructure.Contacts;

/// <summary>The contact read's budget, inside §9.7's bands, since Keycloak is this deployment's own.</summary>
/// <remarks>Public for the reason <c>Program</c> is (§4.2); a send worker's lease must sit above its total.</remarks>
public static class ContactHop
{
    /// <summary>Explicit rather than the typed client's type name, so the pipeline can be read back by it.</summary>
    public const string ClientName = "keycloak-contacts";

    /// <summary>The name the resilience options are filed under (§9.7).</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>The per-attempt bound, inside §9.7's one-to-two-second band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>Retries after the first attempt, so one more request than this; a read is safe to repeat.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Inside §9.7's three-to-five-second band and below <c>ServiceOptions.OperationTimeout</c>.</summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(4.5);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to one worker's call rate rather than the endpoint default's hundred.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>A user representation is a few fields and attributes, so a larger answer is refused unread.</summary>
    public const int MaxAnswerBytes = 64 * 1024;
}
