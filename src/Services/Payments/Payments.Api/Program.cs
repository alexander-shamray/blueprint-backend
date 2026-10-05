using Payments.Api;
using Payments.Api.Endpoints;
using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Provider;
using Common.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start on an unsatisfiable dependency or a captured scope, rather than on the first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddPaymentsApplication();       // §6.2
builder.Services.AddPaymentsInfrastructure(builder.Configuration);   // §4.2, §7.1

// §3.1's anti-corruption layer; its address is read, and its scheme checked, eagerly.
builder.Services.AddPaymentProvider(builder.Configuration, builder.Environment);

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddOpenApi();

// RequirePermission, so the claim type is PermissionClaim.Type's alone (§11.4).
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(PaymentsPermissions.Admin, p => p.RequirePermission(PaymentsPermissions.Admin));

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

app.MapPaymentEndpoints();        // §11.4 — the group fails closed

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory cannot see (§12.4).
public partial class Program;
