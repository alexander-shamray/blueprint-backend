using Shipping.Application;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Carrier;
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
builder.Services.AddShippingApplication();       // §6.2
builder.Services.AddShippingInfrastructure(builder.Configuration);   // §4.2, §7.1

// §3.2's anti-corruption layer; its address is read, and its scheme checked,
// eagerly.
builder.Services.AddCarrierGateway(builder.Configuration, builder.Environment);

// This host registers no permission policy and never will: §3.2 gives it no
// API, so there is no endpoint to name one. The middleware below stays,
// because §11.2 makes every host validate its own token whether or not it
// serves anything, and because ADR-030's fallback policy is what makes the
// probes' AllowAnonymous a decision rather than an omission.

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

// §13.5's probes are the only thing this host serves, and that is the whole
// difference from an API service: everything it does, it does from a hosted
// service. The kubelet reaches this port without a Service in front of it
// (§15.3), which is why there is a listener and no route.
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
