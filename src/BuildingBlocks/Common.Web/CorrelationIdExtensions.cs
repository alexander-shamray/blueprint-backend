using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Common.Web;

/// <summary>The correlation ID every log line, message and trace is filtered by during an incident (§10.4).</summary>
public static class CorrelationIdExtensions
{
    public const string Header = "X-Correlation-Id";

    /// <summary>The longest supplied ID this middleware will adopt (§10.4).</summary>
    public const int MaxSuppliedLength = 128;

    /// <summary>Assigns an ID to a request without one, echoes it and pushes it onto the log scope.</summary>
    /// <remarks>Written onto the request too, as §10.5's body is built after the scope unwinds (§10.4).</remarks>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ILogger logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Common.Web.CorrelationId");

        return app.Use(async (context, next) =>
        {
            string? supplied = context.Request.Headers[Header].FirstOrDefault();

            string correlationId = IsAdoptable(supplied)
                ? supplied
                : Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString();

            context.Request.Headers[Header] = correlationId;

            // From OnStarting, because UseExceptionHandler clears the response before writing its body (§10.4).
            context.Response.OnStarting(
                static state =>
                {
                    (HttpResponse response, string id) = ((HttpResponse, string))state;
                    response.Headers[Header] = id;

                    return Task.CompletedTask;
                },
                (context.Response, correlationId));

            // Not log forging: IsAdoptable has already replaced anything off its alphabet (§10.4).
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
                await next();
        });
    }

    /// <summary>Whether a supplied value is adoptable; anything refused is replaced, never echoed.</summary>
    /// <remarks>A bound on length and alphabet, not a rescue from log splitting (§10.4).</remarks>
    private static bool IsAdoptable([NotNullWhen(true)] string? supplied)
    {
        if (supplied is not { Length: > 0 and <= MaxSuppliedLength })
            return false;

        foreach (char c in supplied)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        return true;
    }
}
