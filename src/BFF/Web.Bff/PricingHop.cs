namespace Web.Bff;

/// <summary>
/// This host's synchronous downstream hop, named in one place (§9.7, ADR-017);
/// it is not the platform's only one (ADR-052).
/// </summary>
public static class PricingHop
{
    /// <summary>
    /// The <see cref="IHttpClientFactory"/> name the gRPC client registers
    /// under, given explicitly rather than defaulted to the client type's name.
    /// </summary>
    /// <remarks>
    /// The resilience options are registered under a name derived from this
    /// one, and a test asserting §9.7's timeout hierarchy has to be able to ask
    /// for them. Defaulting would make that name <c>PricingClient</c> — a
    /// generated identifier, which is a fragile thing for a test to spell.
    /// </remarks>
    public const string ClientName = "catalog-pricing";

    /// <summary>
    /// The options name <c>AddStandardResilienceHandler</c> registers
    /// <c>HttpStandardResilienceOptions</c> under for this client.
    /// </summary>
    /// <remarks>
    /// A convention of the library rather than a contract, so nothing here
    /// trusts it: <c>ResilienceHierarchyTests</c> reads the options through this
    /// name and asserts the configured values. A wrong name yields a
    /// default-constructed instance whose 30 s total request timeout breaches
    /// the hierarchy at once — so the test fails loudly rather than passing
    /// against defaults, which is the direction that matters.
    /// </remarks>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>
    /// Catalog's gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// <b>http, not https</b>: TLS terminates at the ingress and traffic inside
    /// the cluster is plain (§10.1). <b>Port 8081, not 8080</b>: a cleartext
    /// Kestrel endpoint cannot serve HTTP/1.1 and h2c at once — measured, and
    /// argued in <c>Catalog.Api/appsettings.json</c> — so Catalog declares a
    /// second, HTTP/2-only endpoint and this is it. §9.7 printed 8080 and was
    /// amended in this change.
    /// <para>
    /// A literal rather than a configuration key, deliberately. The host is the
    /// Kubernetes Service name, which is the same string in Compose because the
    /// container is named to match — so the value does not differ between
    /// environments, and §15.4's rule is that something which does not vary is
    /// not configuration. It is also the same name YARP routes to (§10.2): the
    /// service's name <i>is</i> the routing configuration, in both files.
    /// </para>
    /// </remarks>
    public static readonly Uri Address = new("http://catalog-api:8081");

    /// <summary>
    /// §9.7's outermost bound for this hop, and the one value in the
    /// hierarchy that must never be left at its default.
    /// </summary>
    /// <remarks>
    /// <b>The resilience values are named here rather than left inline in
    /// <c>Program.cs</c> because §9.7 does arithmetic with them.</b> Its
    /// budget — <see cref="AttemptTimeout"/> per attempt plus
    /// <see cref="MaxRetryDelay"/> per retry, all of it fitting inside
    /// this — is an inequality over several of these at once, so any second
    /// copy of one is a term that can stop holding without either side being
    /// edited. Naming them is what lets the chapter state the sum instead of
    /// computing it. <c>ResilienceHierarchyTests</c> asserts the
    /// relationships from the built options rather than from these constants,
    /// which keeps it a check on the registration.
    /// </remarks>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// HTTP retries after the first attempt, so the pipeline makes one more
    /// request than this. Nothing here is a message delivery — this hop is a
    /// gRPC call over <c>HttpClient</c>, and the broker's vocabulary does not
    /// apply to it.
    /// </summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public const int MaxRetryAttempts = 2;

    /// <summary>The nominal first backoff, before jitter.</summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The cap applied <em>after</em> jitter, which is what makes the budget
    /// arithmetic rather than statistical: the worst case is this times
    /// <see cref="MaxRetryAttempts"/> whatever the draw.
    /// </summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The per-attempt bound inside the total.</summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.4);

    /// <summary>The failure share that opens the circuit.</summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// How many calls a sampling window needs before the ratio is judged.
    /// </summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public const int CircuitBreakerMinimumThroughput = 10;

    /// <summary>
    /// How long the circuit stays open. <c>SamplingDuration</c> is left at
    /// its default, which is longer than this — a window shorter than the
    /// break forgets every failure while the circuit is open, so the breaker
    /// would close onto a fresh window and reopen on the first error it saw.
    /// </summary>
    /// <inheritdoc cref="TotalRequestTimeout" path="/remarks"/>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(15);
}
