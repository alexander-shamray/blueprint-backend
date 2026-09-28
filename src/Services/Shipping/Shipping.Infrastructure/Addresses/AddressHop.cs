namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// The address call's budget, in <c>PricingHop</c>'s shape and inside §9.7's
/// bands: Ordering is a peer, not a third party behind an anti-corruption layer.
/// </summary>
/// <remarks>
/// Public for the reason <c>CarrierHop</c> is (§4.2): read from another
/// assembly, and one modifier commits less than an <c>InternalsVisibleTo</c>.
/// The fulfilment worker's lease sits above this total and the carrier's
/// together (spec, section 4).
/// </remarks>
public static class AddressHop
{
    /// <summary>
    /// The <see cref="IHttpClientFactory"/> name the gRPC client registers
    /// under, given explicitly rather than defaulted to the generated client's
    /// type name, which is a fragile thing for a test to spell.
    /// </summary>
    public const string ClientName = "ordering-delivery-addresses";

    /// <summary>
    /// The name <c>AddStandardResilienceHandler</c> files this client's options
    /// under, so the built pipeline can be read back rather than recomputed
    /// (§9.7).
    /// </summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>The per-attempt bound, inside §9.7's one-to-two-second band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    /// <summary>The nominal first backoff, before jitter.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The cap applied after jitter, which is what makes the budget arithmetic
    /// rather than statistical.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The outermost bound, inside §9.7's three-to-five-second band and below
    /// <c>ServiceOptions.OperationTimeout</c>.
    /// </summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(4.5);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// Sized to a worker's call rate, as the carrier's is: the endpoint
    /// default of a hundred calls in a window is a threshold a loop making one
    /// call every few seconds never reaches, so the breaker would never open.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shorter than the window above, so the breaker does not forget its
    /// failures while open and reopen on the first error after it closes.
    /// </summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);
}
