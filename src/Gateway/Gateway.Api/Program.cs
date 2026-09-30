using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Common.Web;
using Gateway.Api;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2

// §10.1's request size limit; GatewayLimits argues the number.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression; EnableForHttps is true against BREACH (ADR-020).
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// ADR-020's no-transform. Replace, because AddResponseCompression's TryAddSingleton would let order decide.
builder.Services.Replace(
    ServiceDescriptor.Singleton<IResponseCompressionProvider, NoTransformResponseCompressionProvider>());

// §10.2.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// §10.3; this registration does nothing without UseRateLimiter below.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(
        GatewayRateLimiterPolicies.Anonymous,
        context => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // The address fallback is for an anonymous caller on an authenticated route, not cover for pipeline order.
    options.AddPolicy(
        GatewayRateLimiterPolicies.Authenticated,
        context => RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                context.Connection.RemoteIpAddress?.ToString() ??
                "unknown",
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 300,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 10,
                AutoReplenishment = true
            }));

    // Through IProblemDetailsService, so a 429 has the one error shape of §10.5.
    options.OnRejected = async (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                RetryAfterHeader.Seconds(retryAfter).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        IProblemDetailsService problems = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Type = "https://tools.ietf.org/html/rfc6585#section-4"
            }
        });
    };
});

// §10.2's route policies beyond Common.Web's "authenticated", as permission checks for §11.4's reason.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin))
    .AddPolicy(GatewayPermissions.PaymentsAdmin, p => p.RequirePermission(GatewayPermissions.PaymentsAdmin));

// Each is optional, and required once switched on: "on but unconfigured" is a silent defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // Read here, not in the Configure callback, so a missing section fails at startup rather than on a request.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress: opened to all, any client could choose its own rate-limit partition.
        // KnownNetworks carries ASPDEPR005, an error under ADR-019; IPNetwork is qualified past HttpOverrides' own.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly (§10.2); read here for the reason above.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' is enabled but holds no usable origin. An empty or blank entry yields a policy " +
            "matching nothing, so every browser request fails while the host reports healthy (§15.4).");
    }

    if (origins.Any(o => o == "*"))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' contains '*', which cannot be combined with AllowCredentials — ASP.NET Core " +
            "throws when the policy is built, on the first preflight rather than at startup. Name the " +
            "origins, or drop credentials as a deliberate separate decision (§10.2).");
    }

    // One equality with the canonical origin rather than a list of prohibitions: the ways a string can be
    // an origin are finite and the ways it can fail are not. UserInfo is tested apart; the authority keeps it.
    int[] malformed =
    [
        .. origins
            .Select((origin, index) => (origin, index))
            .Where(entry =>
                !Uri.TryCreate(entry.origin, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                parsed.UserInfo.Length > 0 ||
                !string.Equals(entry.origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            .Select(entry => entry.index)
    ];

    // Indexes, never the values: a message reaches the logs, where §13.4's redactor cannot see a secret.
    if (malformed.Length > 0)
    {
        throw new InvalidOperationException(
            $"'Cors:Origins' is not a browser origin at index {string.Join(", ", malformed)}. One is a scheme, " +
            "a host and a port only when it is not the scheme's default, exactly as a browser serialises it — " +
            "WithOrigins compares the configured text, so anything else matches nothing and the host would " +
            "start and refuse every browser (§15.4). The value is deliberately not echoed (§13.4).");
    }

    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // Neither header is CORS-safelisted, so a browser cannot read either without this.
                .WithExposedHeaders("Retry-After", CorrelationIdExtensions.Header)
                .AllowCredentials()));
}

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's header on every response, the exception handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — assigns or replaces the client's

// Above every writer it has to compress, because it works by replacing the response body feature.
app.UseResponseCompression();     // §10.1, ADR-020

// Above the auth pair, because it converts the bodiless challenge and forbid they write.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json

// Above everything that reads the client address; skipped at the edge (Compose), where a forwarded header
// would let a caller choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

// Authentication before the limiter, because §10.3's "authenticated" policy partitions on the subject claim.
app.UseAuthentication();          // §11.3
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();           // §11.4

app.MapReverseProxy();

// The edge owns no database and no broker, so its readiness set is empty (§10.1).
app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an internal Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
