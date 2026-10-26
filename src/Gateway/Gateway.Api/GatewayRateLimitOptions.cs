using Microsoft.Extensions.Options;

namespace Gateway.Api;

/// <summary>§10.3's two budgets, each window or period a minute long.</summary>
/// <remarks>
/// The defaults are the platform's, and no deployment sets the section; ADR-157's stress override does.
/// </remarks>
public sealed class GatewayRateLimitOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "RateLimits";

    /// <summary>Requests one address may make in a window on a route that admits an anonymous caller.</summary>
    public int AnonymousPermitLimit { get; init; } = 100;

    /// <summary>Requests held for the next window; none by default, so a caller over budget is told.</summary>
    public int AnonymousQueueLimit { get; init; }

    /// <summary>The bucket one subject draws on.</summary>
    public int AuthenticatedTokenLimit { get; init; } = 300;

    /// <summary>What a period puts back, the whole bucket by default.</summary>
    public int AuthenticatedTokensPerPeriod { get; init; } = 300;

    /// <summary>Requests held for a token, so a burst at the edge of the bucket waits rather than fails.</summary>
    public int AuthenticatedQueueLimit { get; init; } = 10;
}

/// <summary>Refuses at start a budget the limiter would reject on its first request (§15.4).</summary>
public sealed class GatewayRateLimitOptionsValidator : IValidateOptions<GatewayRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayRateLimitOptions options)
    {
        List<string> failures = [];

        if (options.AnonymousPermitLimit < 1)
            failures.Add($"{nameof(options.AnonymousPermitLimit)} must be at least one.");

        if (options.AuthenticatedTokenLimit < 1)
            failures.Add($"{nameof(options.AuthenticatedTokenLimit)} must be at least one.");

        if (options.AuthenticatedTokensPerPeriod < 1)
            failures.Add($"{nameof(options.AuthenticatedTokensPerPeriod)} must be at least one.");

        if (options.AnonymousQueueLimit < 0)
            failures.Add($"{nameof(options.AnonymousQueueLimit)} must not be negative.");

        if (options.AuthenticatedQueueLimit < 0)
            failures.Add($"{nameof(options.AuthenticatedQueueLimit)} must not be negative.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
