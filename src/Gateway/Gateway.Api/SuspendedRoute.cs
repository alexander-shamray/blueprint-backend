using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Yarp.ReverseProxy.Model;

namespace Gateway.Api;

/// <summary>A route an operator has switched off answers §10.5's 503 at the edge (ADR-116).</summary>
public static class SuspendedRoute
{
    /// <summary>The route metadata key that switches a route off; <c>"true"</c> is the only value that does.</summary>
    public const string MetadataKey = "Suspended";

    /// <summary>§10.5's <c>code</c> for a route switched off on purpose.</summary>
    public const string Code = "service.suspended";

    /// <summary>The clusters ADR-104 lets an operator stop, and so the only ones a route may switch off.</summary>
    public static readonly IReadOnlySet<string> SuspendableClusters =
        new HashSet<string>(StringComparer.Ordinal) { "notifications", "shipping", "privacy" };

    /// <summary>
    /// A client's next look, not a forecast: ADR-104's bound is the operator's to watch, so no time is known (§10.5).
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>Whether <paramref name="metadata"/> switches its route off.</summary>
    public static bool IsSwitchedOff(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null &&
        metadata.TryGetValue(MetadataKey, out string? value) &&
        string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase);

    /// <summary>The proxy pipeline's first step: a switched-off route is answered 503 and forwards nothing.</summary>
    public static async Task AnswerAsync(HttpContext context, Func<Task> next)
    {
        if (!IsSwitchedOff(context.GetRouteModel().Config.Metadata))
        {
            await next();

            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter =
            RetryAfterHeader.Seconds(RetryAfter).ToString(CultureInfo.InvariantCulture);

        IProblemDetailsService problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();

        // Try, so a caller that accepts no problem body still gets the 503 and its header.
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = "This service has been switched off on purpose. Nothing was done; try again later.",
                Extensions = { ["code"] = Code }
            }
        });
    }
}
