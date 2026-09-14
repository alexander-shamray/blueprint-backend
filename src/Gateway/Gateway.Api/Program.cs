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

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2

// §10.1's request size limit; GatewayLimits argues the number. Kestrel
// enforces it where the body is read, which at the edge is inside the
// forwarder, so an oversized request that fails authentication or
// authorization is answered 401 or 403 first. It bounds bytes read, not
// memory: Kestrel and YARP stream the body with backpressure. Past it Kestrel
// throws BadHttpRequestException(413), which ExceptionHandlerMiddleware turns
// into §10.5's problem+json on its own, so no handler is needed beside
// ValidationExceptionHandler and ConcurrencyExceptionHandler.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression. EnableForHttps is true against BREACH; the
// argument is ADR-020's, from content rather than from the scheme. It has to
// be set at all because TLS terminates at the ingress (§10.1):
// UseForwardedHeaders rewrites Request.Scheme to https, and this middleware
// decides at the first write, below the whole pipeline, so it reads the
// rewritten scheme and would otherwise compress nothing, silently.
//
// The providers and the MIME list are the framework's defaults on purpose. The
// default list omits application/problem+json, so §10.5's error bodies — the
// one place a client-supplied value (§10.4) is reflected — travel uncompressed;
// that omission is relied on here and pinned from the wire.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// RFC 9111's no-transform, which ASP.NET Core does not implement and a reverse
// proxy may not ignore: a content coding is a transformation (RFC 9110 §7.7).
// It is also what makes ADR-020's opt-out the standard directive rather than
// Content-Encoding: identity.
//
// Replace rather than registering ahead of AddResponseCompression: that call
// uses TryAddSingleton, so ordering would silently decide this.
builder.Services.Replace(
    ServiceDescriptor.Singleton<IResponseCompressionProvider, NoTransformResponseCompressionProvider>());

// §10.2.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// §10.3. Registration without middleware is the quiet failure mode: this call
// succeeds and does nothing at all if UseRateLimiter is missing below.
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

    // The subject claim, which is empty until UseAuthentication has run — see
    // the pipeline below. The address fallback is for the genuinely anonymous
    // request that still matches an authenticated route, not a safety net for
    // pipeline order: with the order wrong it absorbs every request and this
    // policy degrades to a second copy of the one above with a bigger budget.
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

    // Through IProblemDetailsService rather than WriteAsJsonAsync, so a 429
    // is the same shape as every other error the platform returns (§10.5):
    // application/problem+json, with the correlationId and traceId members
    // AddCommonProblemDetails adds. Writing the body directly produces
    // application/json and none of the three.
    options.OnRejected = async (context, _) =>
    {
        // RetryAfterHeader.Seconds, not a cast: it rounds up, and that file
        // argues why the rule is a type of its own.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                RetryAfterHeader.Seconds(retryAfter).ToString(CultureInfo.InvariantCulture);
        }

        // Set before writing: the customisation reads the response status to
        // fill in the RFC 9457 title and type when they are absent, and the
        // service refuses to write once the response has started.
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

// Every policy §10.2's routes name that Common.Web does not already register:
// "authenticated" comes from AddCommonWebDefaults, and this one is a
// permission check rather than a role check for the reason §11.4 gives. A
// route naming a policy nobody registered fails closed: the config load throws
// out of MapReverseProxy() below, naming the policy and the route.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin));

// Both are conditional on the deployment shape, and each is required once
// switched on: "off" is a valid topology and "on but unconfigured" is a silent
// defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // A load balancer or Ingress sits in front (§15.3), so RemoteIpAddress is
    // the proxy on every request; without this the anonymous limiter puts all
    // traffic into one bucket and its per-client limit becomes a global cap.
    //
    // Read here and not inside the Configure callback: an options callback
    // runs when the options are first resolved, so a missing section read
    // there throws on a request rather than at startup.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        //
        // KnownIPNetworks, not KnownNetworks, which carries ASPDEPR005 at this
        // pin — an error under ADR-019 — and System.Net.IPNetwork qualified,
        // because the HttpOverrides namespace imported for the flags brings
        // its own IPNetwork into scope in place of the one the property takes.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly (§15.4). Read
// here rather than in the AddCors callback for the reason above: the options
// are built on the first request that needs them.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    // GetRequiredSection proves the section exists and nothing more:
    // `Cors__Origins__0=` binds to an array holding one empty string, which
    // WithOrigins accepts, so the host starts and every browser request is
    // rejected by a policy matching no origin. Blank counts as missing, as
    // AddJwtAuthentication already holds for Identity:Authority (§11.3).
    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' is enabled but holds no usable origin. An empty or blank entry yields a policy " +
            "matching nothing, so every browser request fails while the host reports healthy (§15.4).");
    }

    // And "*" separately, for the opposite reason: the policy below calls
    // AllowCredentials(), and ASP.NET Core refuses that pairing when it builds
    // the options — on the first request needing a CORS policy, not at
    // startup.
    if (origins.Any(o => o == "*"))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' contains '*', which cannot be combined with AllowCredentials — ASP.NET Core " +
            "throws when the policy is built, on the first preflight rather than at startup. Name the " +
            "origins, or drop credentials as a deliberate separate decision (§10.2).");
    }

    // Each value has to be the origin a browser will send, and WithOrigins
    // compares the configured text literally — a missing colon, a trailing
    // slash, a path or an explicit default port all start the host healthy
    // and match nothing. One equality rather than a list of prohibitions:
    // GetLeftPart(UriPartial.Authority) is the canonical origin — scheme, host
    // and a port only when it is not the default — so requiring the text to
    // equal it accepts exactly what a browser sends and rejects every variant
    // at once. UserInfo is a separate test because the authority form keeps
    // it.
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

    // Indexes, never the values. Credentials in the authority are one of the
    // shapes rejected above, and an exception message reaches the logs, where
    // §13.4's redactor scrubs keyed attributes and cannot see a secret
    // interpolated into a message. An index is enough for an operator holding
    // the configuration.
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
                // Neither header is CORS-safelisted, so without this a browser
                // cannot read either: Retry-After is what §10.3's rejection
                // handler computes, and CorrelationIdExtensions.Header is on
                // every response, including the 200 or 204 that carries no
                // problem body with its `correlationId` member.
                .WithExposedHeaders("Retry-After", CorrelationIdExtensions.Header)
                .AllowCredentials()));
}

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's one header on every response, including the ones UseExceptionHandler
// writes below: above everything, and written from OnStarting so the handler's
// clear does not take it off the 500.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — assigns or replaces the client's

// High enough to wrap every writer below it — the proxy, the limiter's 429 and
// the status code pages — because this middleware compresses by replacing the
// response body feature, so it can only act on what runs inside it. Moving it
// below the limiter or the auth pair changes nothing observable, since those
// produce only problem+json, which the default MIME list does not compress;
// what matters is its presence, because AddResponseCompression alone
// compresses nothing.
app.UseResponseCompression();     // §10.1, ADR-020

// §10.5's promise for the statuses no handler produces: a challenge and a
// forbid are written by the auth middleware below with no body, and since
// .NET 8 this middleware hands them to the writer AddProblemDetails
// registered. Above the auth pair, because it converts what they write on the
// way back out.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json

// Before everything that reads the client address, and after the two that do
// not: a fault thrown parsing a forwarded header should reach the
// problem-details handler, and anything this middleware logs should run inside
// the correlation scope. Neither of those two reads RemoteIpAddress, so nothing
// is lost by letting them wrap it. Below UseRateLimiter, two forwarded
// addresses would collapse onto the one connection the gateway sees.
//
// Skipped when the gateway is the edge (Compose), where RemoteIpAddress is
// already the client and trusting a forwarded header would let any caller
// choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

// Authentication first, then the limiter, then authorization: §10.3's
// "authenticated" policy partitions on the subject claim, and until this line
// runs HttpContext.User is an empty principal — which does not fail, it meters
// every signed-in caller behind one NAT as a single client.
app.UseAuthentication();          // §11.3
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();           // §11.4

app.MapReverseProxy();

// The edge owns no database and no broker, so its readiness set is empty by
// §10.1's design. Declared at the call site because an empty predicate set
// passes: every other host fails to start without checks.
app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an internal Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
