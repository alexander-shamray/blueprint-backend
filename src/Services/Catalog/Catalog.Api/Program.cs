using Catalog.Api;
using Catalog.Api.Endpoints;
using Catalog.Api.Grpc;
using Catalog.Application;
using Catalog.Infrastructure;
using Common.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// A missing dependency or a singleton capturing a scoped service stops startup rather than a first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddCatalogApplication();       // §6.2
builder.Services.AddCatalogInfrastructure(builder.Configuration);   // §4.2, §7.1

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddOpenApi();

// §9.7's server half. The interceptor is what keeps a malformed request from
// arriving at the caller as Unknown, which the BFF would report as its own
// 500 rather than the caller's 400.
builder.Services.AddGrpc(o => o.Interceptors.Add<ValidationInterceptor>());

// Catalog's permission policies (§11.4). Deliberately not inside either helper
// above: Application knows nothing about HTTP, and Common.Web must not know
// Catalog's names. One policy, because one endpoint names one — the write
// path. §11.4's callout is about the opposite mistake: a name an endpoint uses
// and nobody registered throws on the first request that reaches it, never at
// startup, and the endpoint metadata is what a gate reads to assert both
// directions. RequirePermission rather than RequireClaim("permission", …): the
// claim type is Common.Web's (§11.4), so a policy here and the resource-level
// check behind ICurrentUser cannot drift apart.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(CatalogPermissions.Write, p => p.RequirePermission(CatalogPermissions.Write));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything and written from OnStarting, so the exception handler's 500 carries it.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs

// §10.5's one error shape, for the challenge and forbid the middleware below writes with no body.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();
app.MapProductEndpoints();        // §11.4

// §9.7. Reachable only on the Http2 endpoint appsettings.json declares —
// gRPC needs HTTP/2, and mapping it says nothing about which port serves it.
// The [Authorize] is on the service class, not here, so it travels with the
// type rather than with this line.
app.MapGrpcService<PricingService>();

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory<Program> cannot see (§12.4).
public partial class Program;
