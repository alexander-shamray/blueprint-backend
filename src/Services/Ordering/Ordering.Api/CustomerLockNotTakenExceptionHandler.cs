using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Ordering.Application.Privacy;

namespace Ordering.Api;

/// <summary>Translates <see cref="CustomerLockNotTakenException"/> into §10.5's 503 with <c>Retry-After</c>.</summary>
/// <remarks>Not 500, because the fault is contention and the request took no effect (§10.5).</remarks>
internal sealed class CustomerLockNotTakenExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    // Seconds, a judgement and not a measured figure: the holder is a transaction in flight.
    internal const string RetryAfterSeconds = "5";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not CustomerLockNotTakenException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                // No customer id in the body: the resource name carries one.
                Detail = "Another request for this customer is being applied. Retry.",
                Extensions = { ["code"] = "order.customer_busy" }
            }
        });

        return true;
    }
}
