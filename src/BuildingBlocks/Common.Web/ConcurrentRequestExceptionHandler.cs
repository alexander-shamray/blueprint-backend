using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>Translates <see cref="ConcurrentRequestException"/> into §10.5's in-flight 409 row.</summary>
/// <remarks>The detail says retry rather than wait, because an entry may never complete (§8.5).</remarks>
internal sealed class ConcurrentRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ConcurrentRequestException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No CommandId in the body, because the key it forms carries the subject segment (§8.5).
                Detail = "A request with this command identifier is already in progress. Retry.",
                Extensions = { ["code"] = "request.in_progress" }
            }
        });

        return true;
    }
}
