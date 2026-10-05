using Notifications.Application;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Mail;
using Common.Infrastructure.Identity;
using Common.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// A missing dependency or a singleton capturing a scoped service stops startup rather than a first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddNotificationsApplication();       // §6.2
builder.Services.AddNotificationsInfrastructure(builder.Configuration);   // §4.2, §7.1

// The relay behind IMailChannel (ADR-055); plain or anonymous submission is refused outside Development.
builder.Services.AddMailChannel(builder.Configuration, builder.Environment);

// ADR-052's contact read; its address is read, and its scheme checked, eagerly (ADR-055).
builder.Services.AddContactSource(builder.Configuration, builder.Environment);

// §11.5's client-credentials registrations, this host's own (§15.4, ADR-055).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<CachingTokenClient>();

// ADR-052: this host holds itself to its grant, because the realm gate cannot read a service account's roles.
builder.Services.AddSingleton<ITokenCache>(sp => new GrantCheckedTokenCache(
    sp.GetRequiredService<CachingTokenClient>(),
    sp.GetRequiredService<ContactMetrics>(),
    sp.GetRequiredService<ILogger<GrantCheckedTokenCache>>()));

// Validated at start: IOptions<T> always resolves, so ValidateOnBuild cannot see a forgotten binding (§15.4).
builder.Services
    .AddOptions<ServiceIdentityOptions>()
    .BindConfiguration(ServiceIdentityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The token client's transport carries no ClientCredentialsHandler, which would recurse.
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

builder.Services
    .AddHttpClient(CachingTokenClient.HttpClientName, client =>
        client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// The key's name, so a refused discovery document says which key to fix (§11.3, §11.5).
builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));

// A worker names no endpoint, so it registers no permission policy (§3.2); the token middleware below stays (§11.2).

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

// §13.5's probes are all this host serves, on a port the kubelet reaches directly (§15.3).
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory<Program> cannot see (§12.4).
public partial class Program;
