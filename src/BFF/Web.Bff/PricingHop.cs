namespace Web.Bff;

/// <summary>This host's synchronous downstream hop, named in one place (§9.7, ADR-017).</summary>
public static class PricingHop
{
    /// <summary>Explicit, because the resilience options are registered under a name derived from it.</summary>
    public const string ClientName = "catalog-pricing";

    /// <summary>The options name <c>AddStandardResilienceHandler</c> registers this client's options under.</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>Catalog's gRPC endpoint: plain inside the cluster (§10.1), on its HTTP/2-only port (§9.7).</summary>
    /// <remarks>A literal, because a Service name does not vary between environments (§15.4).</remarks>
    public static readonly Uri Address = new("http://catalog-api:8081");

    /// <summary>§9.7's outermost bound for this hop, never left at its default.</summary>
    /// <remarks>These values are named because §9.7's budget is arithmetic over several at once.</remarks>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>HTTP retries after the first attempt, so the pipeline makes one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The cap applied after jitter, which makes §9.7's budget a bound rather than a statistic.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.4);

    public const double CircuitBreakerFailureRatio = 0.5;

    public const int CircuitBreakerMinimumThroughput = 10;

    /// <summary>How long the circuit stays open; the default sampling window has to outlive it (§9.7).</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(15);
}
