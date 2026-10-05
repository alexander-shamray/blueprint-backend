namespace Gateway.Api;

/// <summary>The edge's request ceilings (§10.1, §9.7), platform decisions rather than framework defaults.</summary>
public static class GatewayLimits
{
    /// <summary>Above a service's deadline, so its 504 arrives first, and inside the host's drain (§9.7).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// One mebibyte: two orders of magnitude above the largest order a client can build, and two below what an
    /// upload endpoint would want (§10.1).
    /// </summary>
    public const long MaxRequestBodyBytes = 1L * 1024 * 1024;
}
