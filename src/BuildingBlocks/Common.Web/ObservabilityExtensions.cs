using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Common.Web;

/// <summary>The three signals of §13.1, configured once for every service host (§13.2).</summary>
public static class ObservabilityExtensions
{
    /// <summary>The SDK's own variable, which AddService would otherwise overwrite (ADR-153).</summary>
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";

    /// <summary>Framework categories logging a line per request or command, held at Warning (§13.4, ADR-159).</summary>
    /// <remarks>By subcategory, so authentication's and authorization's reasons for a 401 or 403 still log.</remarks>
    public static IReadOnlyList<string> FrameworkCategories { get; } =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Http.Result",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "System.Net.Http.HttpClient",
        "Yarp.ReverseProxy.Forwarder"
    ];

    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string assembly = builder.Environment.ApplicationName;

        // ADR-153's assistant release runs the BFF's image, and the canary selects its baseline by this name.
        string serviceName = builder.Configuration[ServiceNameKey] is { } named && !string.IsNullOrWhiteSpace(named)
            ? named.Trim()
            : assembly;

        // "Catalog.Api" and "Catalog.Worker" both own "Catalog.*"; a name with no dot owns itself. The assembly's,
        // since the meters are the code's whatever the release is called.
        string servicePrefix = assembly.Split('.')[0];

        // The only provider, because the redactor sees records in this pipeline and nowhere else (§13.4).
        builder.Logging.ClearProviders();

        // Wraps whatever scope provider is registered, so every provider's scopes are redacted (§13.4).
        RedactingScopeProvider.WrapScopesForRedaction(builder.Services);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // §13.4's "never log a secret" rule, given a mechanism.
            logging.AddProcessor(new SensitiveDataRedactor());
        });

        // Each repeats what the request's span carries, at a rate that filled the LGTM container's log queue under
        // load (ADR-159). A code filter outranks a configured one of the same category, so a configured level skips it.
        IEnumerable<string> unconfigured =
            FrameworkCategories.Where(c => builder.Configuration[$"Logging:LogLevel:{c}"] is null);

        foreach (string category in unconfigured)
            builder.Logging.AddFilter(category, LogLevel.Warning);

        KeyValuePair<string, object> environment =
            new("deployment.environment", builder.Environment.EnvironmentName);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: BuildInfo.Version)
                .AddAttributes([environment]))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // A condition whose meter is not registered here cannot fire (§13.6). A host exports its own
                // service's meters by prefix, so this block names no service (ADR-062).
                .AddMeter($"{servicePrefix}.*")

                // Shared names, not service-prefixed: the service.name resource attribute separates them.
                .AddMeter("Commerce.Requests")                     // §13.3, §13.7
                .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
                .AddMeter("MassTransit")
                // Publishes no meter at the pinned version; §13.6 records what the alert is owed.
                .AddMeter("Microsoft.Extensions.Caching.Hybrid")
                .AddMeter("StackExchange.Redis"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // No options: SetDbQueryParameters would put raw values on the span, past §13.4's redactor (§13.2).
                .AddEntityFrameworkCoreInstrumentation()
                // Redis instrumentation lives in AddRedisConnections, beside the keyed connections (§13.2).
                .AddSource("MassTransit")
                // OutboxDispatcher.ActivitySourceName: the span that joins a staging request to its publish (§9.4).
                .AddSource("Commerce.Outbox")
                // StagedTrace.ClaimSourceName: a worker's pass over a row it claims, joined to the row's writer (§9.4).
                .AddSource("Commerce.Claims"))
            .UseOtlpExporter();

        return builder;
    }
}
