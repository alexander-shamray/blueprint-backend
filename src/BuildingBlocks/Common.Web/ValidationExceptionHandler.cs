using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Common.Web;

/// <summary>Translates <c>ValidationException</c> into §10.5's field-keyed 400 row.</summary>
internal sealed class ValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validation)
            return false;

        Dictionary<string, string[]> errors = validation.Errors
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                string[] (g) => [.. g.Select(f => f.ErrorMessage)],
                StringComparer.Ordinal);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        // Handled whatever the writer negotiates, so a refused Accept header does not fall through to 500.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest
            }
        });

        return true;
    }
}
