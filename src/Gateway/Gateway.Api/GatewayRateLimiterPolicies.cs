namespace Gateway.Api;

/// <summary>The rate-limiter policy names of §10.3, which every route in §10.2 must name one of.</summary>
/// <remarks>
/// <c>Authenticated</c> is not the authorization policy of the same name; §10.2 keeps the two registries apart.
/// </remarks>
public static class GatewayRateLimiterPolicies
{
    /// <summary>Per-IP fixed window, for routes that admit an anonymous caller.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Per-subject token bucket, falling back to the address (§10.3).</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Every policy <c>Program.cs</c> registers, in one place a test can read.</summary>
    public static readonly IReadOnlyList<string> All = [Anonymous, Authenticated];
}
