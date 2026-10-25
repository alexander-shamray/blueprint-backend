namespace Web.Bff;

/// <summary>This host's availability hop to Inventory, named in one place (§9.7, ADR-154).</summary>
/// <remarks>The budget is <see cref="PricingHop"/>'s, value for value: the two hops are one shape.</remarks>
public static class StockHop
{
    /// <summary>Explicit, because the resilience options are registered under a name derived from it.</summary>
    public const string ClientName = "inventory-stock";

    /// <summary>The options name <c>AddStandardResilienceHandler</c> registers this client's options under.</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>Inventory's gRPC endpoint: plain inside the cluster (§10.1), on its HTTP/2-only port (§9.7).</summary>
    /// <remarks>A literal: the host is the Kubernetes Service name, which does not vary (§15.4).</remarks>
    public static readonly Uri Address = new("http://inventory-api:8081");

    public static readonly TimeSpan TotalRequestTimeout = PricingHop.TotalRequestTimeout;

    public const int MaxRetryAttempts = PricingHop.MaxRetryAttempts;

    public static readonly TimeSpan RetryDelay = PricingHop.RetryDelay;

    public static readonly TimeSpan MaxRetryDelay = PricingHop.MaxRetryDelay;

    public static readonly TimeSpan AttemptTimeout = PricingHop.AttemptTimeout;

    public const double CircuitBreakerFailureRatio = PricingHop.CircuitBreakerFailureRatio;

    public const int CircuitBreakerMinimumThroughput = PricingHop.CircuitBreakerMinimumThroughput;

    public static readonly TimeSpan CircuitBreakerBreakDuration = PricingHop.CircuitBreakerBreakDuration;
}
