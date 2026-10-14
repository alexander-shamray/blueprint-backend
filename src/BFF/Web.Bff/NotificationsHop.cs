namespace Web.Bff;

/// <summary>This host's second synchronous hop, ADR-117's registration, named in one place (§9.7, ADR-017).</summary>
/// <remarks>Its own numbers, not <see cref="PricingHop"/>'s, so neither budget is a term in the other.</remarks>
public static class NotificationsHop
{
    /// <summary>Explicit, because the resilience options are registered under a name derived from it.</summary>
    public const string ClientName = "notifications-devices";

    /// <summary>The options name <c>AddStandardResilienceHandler</c> registers this client's options under.</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>Notifications.Api's gRPC endpoint: plain inside the cluster (§10.1), on its HTTP/2-only port.</summary>
    /// <remarks>A literal: the host is the Kubernetes Service name, which does not vary (§15.4).</remarks>
    public static readonly Uri Address = new("http://notifications-api:8081");

    /// <summary>§9.7's outermost bound for this hop, never left at its default.</summary>
    /// <remarks>These values are named because §9.7's budget is arithmetic over several at once.</remarks>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>HTTP retries after the first attempt; both methods are convergent, so a retry is harmless.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The cap applied after jitter, which makes §9.7's budget a bound rather than a statistic.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>One row under a held lock, so the attempt is sized as a peer's, inside §9.7's band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.4);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Every app start registers, so the rate is the buyers', as the quote's is.</summary>
    public const int CircuitBreakerMinimumThroughput = 10;

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(15);
}
