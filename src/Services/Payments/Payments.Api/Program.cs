using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Provider;
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
builder.Services.AddPaymentsApplication();       // §6.2
builder.Services.AddPaymentsInfrastructure(builder.Configuration);   // §4.2, §7.1

// §3.2's anti-corruption layer; its address is read, and its scheme checked,
// eagerly.
builder.Services.AddPaymentProvider(builder.Configuration, builder.Environment);

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddOpenApi();

// This service registers no permission policy, because it names no endpoint
// that needs one; the first slice brings both together (§11.4). A policy
// registered before an endpoint names it is an unused registration, and an
// endpoint naming one nobody registered throws on the first request that
// reaches it, never at startup.

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
// a forbid are written by the middleware below and carry no body, so without
// this the platform's one error shape would have two holes in it.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

// This service maps no endpoint of its own yet. The first one goes here,
// behind RequireAuthorization at the group (§11.4) — fail closed, and let
// any deliberately public endpoint say AllowAnonymous out loud.

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
