using Inventory.Api;
using Inventory.Api.Endpoints;
using Inventory.Application;
using Inventory.Infrastructure;
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
builder.Services.AddInventoryApplication();       // §6.2
builder.Services.AddInventoryInfrastructure(builder.Configuration);   // §4.2, §7.1

// PR-07's OpenAPI deliverable (Appendix C): document only, no UI.
builder.Services.AddOpenApi();

// RequirePermission rather than RequireClaim("permission", …): the claim type
// is PermissionClaim.Type, and spelling the literal here would be a fourth
// place that has to agree with it (§11.4).
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(InventoryPermissions.Admin, p => p.RequirePermission(InventoryPermissions.Admin));

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

app.MapStockEndpoints();          // §11.4 — the group fails closed
app.MapReservationEndpoints();    // §11.4 — the runbook's three admin routes

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
