using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Common.Web;

/// <summary>The three probes Kubernetes asks three distinct questions through (§13.5).</summary>
public static class HealthCheckExtensions
{
    // Spelled once, so the predicates and the startup guard ask about the same set.
    private const string Ready = "ready";

    /// <summary>Maps the live, ready and startup probes, refusing an empty readiness set by default.</summary>
    /// <remarks>An empty predicate set passes, so a host with none must say so at the call site (§13.5).</remarks>
    public static IEndpointRouteBuilder MapCommonHealthEndpoints(
        this IEndpointRouteBuilder app,
        bool ownsNoReadinessDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!ownsNoReadinessDependencies && !AnyReadinessCheck(app))
        {
            throw new InvalidOperationException(
                "No health check carries the \"ready\" tag, so /health/ready would answer 200 " +
                "without having verified anything (§13.5). Register the service's readiness " +
                "checks in its own Infrastructure, or pass ownsNoReadinessDependencies: true if this host " +
                "gates readiness on nothing.");
        }

        // The kubelet sends no token, so an authenticated probe restarts the pod in a loop.
        app
            .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        return app;
    }

    // The options, because they are what the predicates above are evaluated against.
    private static bool AnyReadinessCheck(IEndpointRouteBuilder app)
    {
        HealthCheckServiceOptions options = app.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        return options.Registrations.Any(r => r.Tags.Contains(Ready));
    }
}
