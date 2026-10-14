using Notifications.Api.Grpc;
using Notifications.Application;
using Notifications.Infrastructure;
using Common.Web;

if (args is [HealthProbe.Argument]) Environment.Exit(await HealthProbe.RunAsync());   // §14.1's healthcheck

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// A missing dependency or a singleton capturing a scoped service stops startup rather than a first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2

// This host's half of each layer and nothing else of the worker's: no consumer, no loop, no meter (ADR-118).
builder.Services.AddNotificationsApplication(NotificationsHost.Api);   // §6.2
builder.Services.AddNotificationsInfrastructure(builder.Configuration, NotificationsHost.Api);   // §4.2

// §9.7's server half. The interceptor keeps a malformed request from reaching the BFF as Unknown, its 500.
builder.Services.AddGrpc(o => o.Interceptors.Add<ValidationInterceptor>());

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything and written from OnStarting, so the exception handler's 500 carries it.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs
app.UseRequestTimeouts();         // §9.7 — below the exception handler, which would answer 499

// §10.5's one error shape, for the challenge and forbid the middleware below writes with no body.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token

// ADR-117. Reachable only on the Http2 endpoint appsettings.json declares; both methods are ordered by the
// installation's sequence, so a repeat is answered as the first was and a stale one changes nothing.
app.MapGrpcService<DevicesService>().RetrySafe(RetrySafety.Convergent);

// ADR-120. A write is appended only while the entry it read is the newest, so its repeat is answered as it was;
// the read beside it writes nothing, which is convergent too, and one declaration covers the service.
app.MapGrpcService<PreferencesService>().RetrySafe(RetrySafety.Convergent);

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory<Program> cannot see (§12.4).
public partial class Program;
