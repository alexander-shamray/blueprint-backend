using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Common.Web;

/// <summary>
/// The three signals of §13.1, configured once here and referenced by every
/// service host (§13.2). Composed by <c>AddCommonWebDefaults</c> rather than
/// called directly by a host.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers the logging pipeline, metrics, tracing and OTLP export.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName;

        // OpenTelemetry becomes the ONLY logging provider, and that is a
        // security requirement rather than tidiness. SensitiveDataRedactor is a
        // BaseProcessor<LogRecord>: it sees records inside this pipeline and
        // nowhere else. WebApplication.CreateBuilder installs Console, Debug
        // and EventSource before a host reaches this line (§4.2), each of which
        // formats the original state itself — so a {Password} scrubbed on the
        // OTLP path still shipped in clear text on the console one, and
        // container stdout is collected in most clusters. Verified by test.
        builder.Logging.ClearProviders();

        // The scope half of §13.4, and it has to be registered rather than
        // configured: LoggerFactory takes an IExternalScopeProvider from the
        // container and hands the same instance to every provider, so wrapping
        // it here covers scopes opened by EF Core and MassTransit as well as
        // the platform's own two. IncludeScopes below is what puts them on the
        // record; without this line the redactor would be scrubbing attributes
        // beside a scope carrying whatever the caller sent.
        //
        // It WRAPS whatever is already registered rather than standing aside
        // for it. TryAdd was the first spelling and it fails open: a host that
        // had registered any provider first kept it, unwrapped, and every scope
        // exported raw while IncludeScopes stayed on and the redactor went on
        // scrubbing attributes beside it — a security control switched off by a
        // registration nobody looked at. The comment beside it was wrong in the
        // other direction too, claiming a later registration would be the one
        // ignored; the built-in container resolves the LAST, measured rather
        // than assumed.
        RedactingScopeProvider.WrapScopesForRedaction(builder.Services);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // §13.4's "never log a secret" rule, given a mechanism. Registered
            // here because this is the only logging pipeline the host has — a
            // redaction policy configured on a library nobody installed
            // redacts nothing, and reads in review as though it does.
            logging.AddProcessor(new SensitiveDataRedactor());
        });

        // A named local rather than an inline construction. The type has to be
        // spelled out where it is built, and spelling it inside AddAttributes'
        // own argument runs that line to 130 columns — past the 120 budget.
        // Named here, the declaration carries the type and `new` needs none.
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
                // Every meter the platform exports, so every one an alert or
                // SLO reads from. A condition whose signal is not registered
                // here cannot fire — it looks configured and is silent, which
                // is worse than having no alert at all. Registered ahead of the
                // instruments: a name is a string and costs nothing, and the
                // alternative is spreading one block's edits across six later
                // pull requests.
                .AddMeter("Ordering.Orders")                       // §13.3, §13.6
                .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
                .AddMeter("Inventory.Reservations")                // §13.3
                .AddMeter("Inventory.Outbox")                      // §13.6 per-lane
                .AddMeter("Payments.Provider")                     // §3.2's provider

                // Shared names, not service-prefixed: every service emits the
                // same instruments and the service.name resource attribute
                // separates them. One dashboard query then works for all of
                // them, and a new service appears on it without anyone editing
                // a panel.
                .AddMeter("Commerce.Requests")                     // §13.3, §13.7
                .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
                .AddMeter("MassTransit")
                // §13.6's cache-hit-ratio alert. COLLECTS NOTHING AT THE
                // PINNED VERSION, and the line stays with this comment rather
                // than being deleted: Microsoft.Extensions.Caching.Hybrid
                // 10.0.0 references System.Diagnostics.Tracing and not
                // System.Diagnostics.Metrics — it publishes through
                // HybridCacheEventSource with PollingCounter, so there is no
                // Meter behind this name. Measured against the package, not
                // assumed. The alert is unloaded in awaiting-signal.yaml and
                // §13.6 records what it is owed; a registered meter with no
                // publisher is the trap that section spends a callout on, and
                // this is the platform's own instance of it.
                .AddMeter("Microsoft.Extensions.Caching.Hybrid")
                .AddMeter("StackExchange.Redis"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // Landed with PR-08, the PR that gave a service a DbContext —
                // §13.2's rule is that an instrumentation arrives with the
                // package it instruments, and EF Core is now here.
                //
                // No options lambda, and §13.2 was amended to match. The
                // chapter configured SetDbStatementForText, which this package
                // line does not have: the command text rides on the
                // semantic-convention attributes and is emitted by default.
                // The package's XML also documents a SetDbQueryParameters
                // switch — raw parameter VALUES on the span, past §13.4's
                // redactor — but the compiler rejects it on this pin, so
                // nothing here can turn it on. If a bump ever exposes it, it
                // stays off.
                .AddEntityFrameworkCoreInstrumentation()
                // AddRedisInstrumentation landed at PR-12 inside
                // AddRedisConnections (§8.2), not here, and permanently so:
                // §8.1's connections are keyed services, the parameterless
                // overload discovers only an unkeyed IConnectionMultiplexer —
                // in this block it would silently instrument nothing, and it
                // would hand StackExchange.Redis to hosts that have no Redis.
                .AddSource("MassTransit"))
            .UseOtlpExporter();

        return builder;
    }
}
