using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Web.Bff;

/// <summary>§9.7's third rule for this host: a clear error for an upstream failure, decided in advance.</summary>
internal sealed class UpstreamExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not RpcException rpc)
            return false;

        (int status, string title) = rpc.StatusCode switch
        {
            // The BFF built the request from the caller's own basket, so the caller has to change something.
            StatusCode.InvalidArgument => (StatusCodes.Status400BadRequest, "Invalid pricing request"),

            // 503 rather than 502, because it is the status that says "try again later".
            StatusCode.Unavailable or StatusCode.DeadlineExceeded =>
                (StatusCodes.Status503ServiceUnavailable, "Pricing is temporarily unavailable"),

            // A failure in the client pipeline has no gRPC status: Grpc.Net.Client raises Internal and keeps the
            // cause in Status.DebugException.
            StatusCode.Internal when IsTransient(rpc.Status.DebugException) =>
                (StatusCodes.Status503ServiceUnavailable, "Pricing is temporarily unavailable"),

            _ => (StatusCodes.Status500InternalServerError, "Pricing failed")
        };

        // Left unhandled rather than written as a 500, so it is logged and reaches §13.2's error metrics.
        if (status == StatusCodes.Status500InternalServerError)
            return false;

        httpContext.Response.StatusCode = status;

        // The gRPC detail is not copied: §10.5 gives a client one error shape, not a passthrough.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title
            }
        });

        return true;
    }

    /// <summary>The causes the resilience pipeline ends with, a closed list so any other fault stays a 500.</summary>
    private static bool IsTransient(Exception? cause) =>
        cause is HttpRequestException or TimeoutRejectedException or BrokenCircuitException;
}
