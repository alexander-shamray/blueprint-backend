using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>§10.6's response security headers; the ones deliberately absent are ADR-031's.</summary>
public static class SecurityHeadersExtensions
{
    private const string ContentTypeOptions = "X-Content-Type-Options";
    private const string NoSniff = "nosniff";

    /// <summary>Adds <c>X-Content-Type-Options: nosniff</c> to every response, called outermost (§4.2).</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The RequestDelegate overload, which allocates nothing per request (ADR-031).
        return app.Use((HttpContext context, RequestDelegate next) =>
        {
            // From OnStarting, because UseExceptionHandler clears the response before writing its body (§10.6).
            context.Response.OnStarting(
                static state =>
                {
                    HttpResponse response = (HttpResponse)state;

                    // Indexer rather than Append, so the header is never sent twice.
                    response.Headers[ContentTypeOptions] = NoSniff;

                    return Task.CompletedTask;
                },
                context.Response);

            return next(context);
        });
    }
}
