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
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName;

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
                // A condition whose meter is not registered here cannot fire (§13.6).
                .AddMeter("Catalog.Outbox")                        // §13.6 per-lane
                .AddMeter("Ordering.Orders")                       // §13.3, §13.6
                .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
                .AddMeter("Inventory.Reservations")                // §13.3
                .AddMeter("Inventory.Outbox")                      // §13.6 per-lane
                .AddMeter("Payments.Provider")                     // §3.2's provider
                .AddMeter("Payments.Outbox")                       // §13.6 per-lane
                .AddMeter("Shipping.Outbound")                     // §3.2's carrier, and the address read
                .AddMeter("Shipping.Outbox")                       // §13.6 per-lane
                .AddMeter("Notifications.Outbound")                // Notifications' outbound calls (§3.2)
                .AddMeter("Web.Bff.Projection")                    // ADR-051's projection

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
                .AddSource("MassTransit"))
            .UseOtlpExporter();

        return builder;
    }
}
