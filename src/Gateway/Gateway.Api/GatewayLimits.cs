namespace Gateway.Api;

/// <summary>The edge's request body ceiling (§10.1), a platform decision rather than a framework default.</summary>
public static class GatewayLimits
{
    /// <summary>
    /// One mebibyte: two orders of magnitude above the largest order a client can build, and two below what an
    /// upload endpoint would want (§10.1).
    /// </summary>
    public const long MaxRequestBodyBytes = 1L * 1024 * 1024;
}
