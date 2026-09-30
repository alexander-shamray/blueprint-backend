namespace Gateway.Api;

/// <summary>The <c>Retry-After</c> seconds §10.3's rejection handler sends; §10.3 argues why it is a type.</summary>
public static class RetryAfterHeader
{
    /// <summary>Rounded up, since truncating can name a time the limiter still refuses; never below zero.</summary>
    public static int Seconds(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
}
