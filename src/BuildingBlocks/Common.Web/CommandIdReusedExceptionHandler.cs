using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>Translates <see cref="CommandIdReusedException"/> into §10.5's reused-identifier 409 row.</summary>
/// <remarks>The detail asks for a new identifier, because a retry meets the same refusal (ADR-057).</remarks>
internal sealed class CommandIdReusedExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not CommandIdReusedException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No CommandId in the body, because the key it forms carries the subject segment (§8.5).
                Detail =
                    "This command identifier was already used for a different request; " +
                    "send a changed request under a new identifier.",
                Extensions = { ["code"] = "command.id_reused" }
            }
        });

        return true;
    }
}
