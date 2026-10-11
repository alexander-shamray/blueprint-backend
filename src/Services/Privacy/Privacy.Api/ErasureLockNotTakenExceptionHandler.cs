using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Privacy.Application.ErasureRequests;

namespace Privacy.Api;

/// <summary>Translates <see cref="ErasureLockNotTakenException"/> into §10.5's 503 with <c>Retry-After</c>.</summary>
/// <remarks>Not 500, because the fault is contention and the request took no effect (§10.5).</remarks>
internal sealed class ErasureLockNotTakenExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    // Seconds, a little over one request's own duration, since the lock's holder is a transaction in flight.
    internal const string RetryAfterSeconds = "5";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ErasureLockNotTakenException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                // No subject or request id in the body: the resource name carries one.
                Detail = "The erasure request is being updated by another request. Retry.",
                Extensions = { ["code"] = "erasure_request.busy" }
            }
        });

        return true;
    }
}
