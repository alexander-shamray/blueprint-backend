using Ordering.Api;
using Ordering.Api.Endpoints;
using Ordering.Api.Grpc;
using Ordering.Application;
using Ordering.Infrastructure;
using Common.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start on an unsatisfiable dependency or a captured scope, rather than on the first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddOrderingApplication();       // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);   // §4.2, §7.1

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddOpenApi();

// ADR-052's server half; no interceptor, as the only caller-supplied value is parsed before the dispatcher.
builder.Services.AddGrpc();

// RequirePermission, so the claim type is PermissionClaim.Type's alone (§11.4). No orders:admin policy: that string
// is a claim CancelOrderHandler checks against a loaded aggregate.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything, so every response carries it, the handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs

// §10.5's error shape for the bodiless challenge and forbid the middleware below writes.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

app.MapOrderEndpoints();          // §11.4 — the group fails closed

// ADR-052, on the Http2 endpoint appsettings.json declares; [Authorize] travels on the service class.
app.MapGrpcService<DeliveryAddressService>().RetrySafe(RetrySafety.ReadOnly);   // ADR-052 — Get reads one address

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory cannot see (§12.4).
public partial class Program;
