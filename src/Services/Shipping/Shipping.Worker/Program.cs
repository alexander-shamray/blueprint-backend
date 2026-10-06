using Shipping.Application;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Carrier;
using Common.Infrastructure.Identity;
using Common.Web;

if (args is [HealthProbe.Argument]) Environment.Exit(await HealthProbe.RunAsync());   // §14.1's healthcheck

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start on an unsatisfiable dependency or a captured scope, rather than on the first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddShippingApplication();       // §6.2
builder.Services.AddShippingInfrastructure(builder.Configuration);   // §4.2, §7.1

// §3.1's anti-corruption layer; its address is read, and its scheme checked, eagerly.
builder.Services.AddCarrierGateway(builder.Configuration, builder.Environment);

// §9.7, §11.5 — this host's client-credentials registrations (ADR-052); the binding is its own (§15.4).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<CachingTokenClient>();

// ADR-052: this host holds itself to its grant, because the realm gate cannot read a service account's roles.
builder.Services.AddSingleton<ITokenCache>(sp =>
    new GrantCheckedTokenCache(
        sp.GetRequiredService<CachingTokenClient>(),
        sp.GetRequiredService<AddressMetrics>(),
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
    .AddHttpClient(
        CachingTokenClient.HttpClientName,
        client => client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// The key's name, so a refused discovery document says which key to fix (§11.3, §11.5).
builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));

// ADR-052's read; its address is read, and checked, eagerly.
builder.Services.AddDeliveryAddressSource(builder.Configuration);

// No permission policy, as §3.2 gives this host no API; the auth middleware stays, per §11.2 and ADR-030.

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything, so every response carries it, the handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs
app.UseRequestTimeouts();         // §9.7 — below the exception handler, which would answer 499

// §10.5's error shape for the bodiless challenge and forbid the middleware below writes.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

// §13.5's probes are all this host serves; the kubelet reaches the port with no Service in front (§15.3).
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory<Program> cannot see (§12.4).
public partial class Program;
