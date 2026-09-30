using Common.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>Translates <see cref="CommandAlreadyCommittedException"/> into §10.5's non-retryable 409 row.</summary>
/// <remarks>409 rather than 200 or 500, and the detail says read rather than retry (§10.5, ADR-037).</remarks>
internal sealed class CommandAlreadyCommittedExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not CommandAlreadyCommittedException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                // No key in the body, because it carries the subject segment (§8.5).
                Detail =
                    "This command has already been applied and its result is no longer " +
                    "available; read the resource rather than retrying.",
                Extensions = { ["code"] = "command.already_committed" }
            }
        });

        return true;
    }
}
