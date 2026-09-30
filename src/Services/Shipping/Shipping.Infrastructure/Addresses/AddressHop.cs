namespace Shipping.Infrastructure.Addresses;

/// <summary>The address call's budget, inside §9.7's bands, since Ordering is a peer and not a third party.</summary>
/// <remarks>Public for the reason <see cref="Carrier.CarrierHop"/> is (§4.2).</remarks>
public static class AddressHop
{
    /// <summary>Explicit rather than the generated client's type name, which is fragile to spell.</summary>
    public const string ClientName = "ordering-delivery-addresses";

    /// <summary>The name the resilience options are filed under, so the pipeline can be read back (§9.7).</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>The per-attempt bound, inside §9.7's one-to-two-second band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Inside §9.7's three-to-five-second band and below <c>ServiceOptions.OperationTimeout</c>.</summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(4.5);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to a worker's call rate, as <see cref="Carrier.CarrierHop"/>'s is.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>Shorter than the window, so the breaker keeps its failures while open.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);
}
