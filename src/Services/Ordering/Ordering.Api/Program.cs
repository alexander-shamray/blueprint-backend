using Ordering.Api;
using Ordering.Api.Endpoints;
using Ordering.Api.Grpc;
using Ordering.Application;
using Ordering.Infrastructure;
using Common.Web;

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
builder.Services.AddOrderingApplication();       // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);   // §4.2, §7.1

// PR-07's OpenAPI deliverable (Appendix C): document only, no UI.
builder.Services.AddOpenApi();

// ADR-052's server half. No interceptor: this service has no validator on the
// query, so nothing throws a ValidationException for one to translate — the
// only caller-supplied value is parsed above the dispatcher and refused there.
builder.Services.AddGrpc();

// RequirePermission rather than RequireClaim("permission", …): the claim type
// is PermissionClaim.Type, and spelling the literal here would be a fourth
// place that has to agree with it (§11.4).
//
// One policy per name in OrderingPermissions; ADR-052's is a gRPC method's and
// no endpoint route names it. There is deliberately no orders:admin policy —
// that string is a claim, read by CancelOrderHandler against a loaded
// aggregate, and §11.4 is emphatic that a policy nobody registered resolves
// to nothing.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's one header: nosniff on every response, including the ones
// UseExceptionHandler writes below. Above everything, so nothing can answer
// without it — and written from OnStarting, so the handler's clear does not
// take it off the 500.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs

// §10.5's promise applied to the statuses no handler produces: a challenge and
// a forbid are written by the middleware below and carry no body, so the
// platform's one error shape had two holes in it until PR-17 measured a 401.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

app.MapOrderEndpoints();          // §11.4 — the group fails closed

// ADR-052. Reachable only on the Http2 endpoint appsettings.json declares —
// gRPC needs HTTP/2, and mapping it says nothing about which port serves it.
// The [Authorize] is on the service class, not here, so it travels with the
// type rather than with this line.
app.MapGrpcService<DeliveryAddressService>();

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
