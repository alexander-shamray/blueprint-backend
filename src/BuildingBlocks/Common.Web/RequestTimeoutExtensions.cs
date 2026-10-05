using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Web;

/// <summary>§9.7's request deadline, answered with §10.5's 504 row.</summary>
/// <remarks>
/// Cooperative: it cancels <c>RequestAborted</c>, so a handler that drops its token runs on. An endpoint opts out
/// with <c>DisableRequestTimeout</c> (§9.7).
/// </remarks>
public static class RequestTimeoutExtensions
{
    /// <summary>§10.5's <c>code</c> for a request the host ended at its deadline.</summary>
    public const string TimedOutCode = "request.timed_out";

    public static IServiceCollection AddCommonRequestTimeouts(this IServiceCollection services, TimeSpan timeout) =>
        services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = timeout,
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
            WriteTimeoutResponse = WriteTimedOutAsync
        });

    private static async Task WriteTimedOutAsync(HttpContext context)
    {
        IProblemDetailsService problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                // Its effect is unknown, since the deadline may fall after a commit (§10.5).
                Detail = "The request did not complete within the deadline. Whether it took effect is unknown.",
                Extensions = { ["code"] = TimedOutCode }
            }
        });
    }
}
