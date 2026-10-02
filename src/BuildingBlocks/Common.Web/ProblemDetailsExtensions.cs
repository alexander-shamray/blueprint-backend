using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Web;

/// <summary>RFC 9457 <c>application/problem+json</c> for every service (§10.5).</summary>
public static class ProblemDetailsExtensions
{
    /// <summary>Registers the shared customisation and an executor for §10.5's 400 and 409 rows.</summary>
    public static IServiceCollection AddCommonProblemDetails(this IServiceCollection services)
    {
        services.AddExceptionHandler<ValidationExceptionHandler>();

        services.AddExceptionHandler<ConcurrencyExceptionHandler>();

        services.AddExceptionHandler<ConcurrentRequestExceptionHandler>();

        services.AddExceptionHandler<CommandAlreadyCommittedExceptionHandler>();

        services.AddExceptionHandler<CommandIdReusedExceptionHandler>();

        services.AddExceptionHandler<BadHttpRequestExceptionHandler>();

        // Every environment raises a binding refusal, so the answer cannot vary with IsDevelopment (§10.5);
        // PostConfigure, since routing's own default is a Configure that may be registered after this call.
        services.PostConfigure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        return services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance =
                    $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

                // From the request, which survives an unwinding exception where the log scope does not (§10.4).
                context.ProblemDetails.Extensions["correlationId"] =
                    context.HttpContext.Request.Headers[CorrelationIdExtensions.Header].FirstOrDefault();

                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            });
    }
}
