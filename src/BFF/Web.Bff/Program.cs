using Catalog.Pricing.V1;
using Common.Infrastructure.Identity;
using Common.Web;
using FluentValidation;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Web.Bff;
using Web.Bff.Endpoints;
using Web.Bff.Messaging;
using Web.Bff.Orders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2

// §9.7's fallback for this host's outbound call, so it lives here rather than in Common.Web.
builder.Services.AddExceptionHandler<UpstreamExceptionHandler>();

// §6.4's validator; with no handler pipeline in this host, the endpoint calls it itself.
builder.Services.AddSingleton<IValidator<QuoteRequest>, QuoteRequestValidator>();

// §9.7, §11.5 — this host's client-credentials registrations (ADR-052); the binding is its own (§15.4).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<ITokenCache, CachingTokenClient>();

builder.Services.AddSingleton(TimeProvider.System);

// ADR-051's projection: its schema, its inbox purge and its readiness check (§7.1, §9.5, §13.5).
builder.Services.AddBffPersistence(builder.Configuration);

// Validated at start: IOptions<T> always resolves, so ValidateOnBuild cannot see a forgotten binding (§15.4).
builder.Services
    .AddOptions<ServiceIdentityOptions>()
    .BindConfiguration(ServiceIdentityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The authority §11.3 validates against. The token client's transport carries no ClientCredentialsHandler,
// which would recurse, and its trailing slash keeps a relative discovery path inside the realm.
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));

builder.Services
    .AddHttpClient(CachingTokenClient.HttpClientName, client =>
        client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// A local rather than one chain, because AddStandardResilienceHandler returns a different builder (§9.7).
IHttpClientBuilder pricing = builder.Services
    .AddGrpcClient<Pricing.PricingClient>(PricingHop.ClientName, o => o.Address = PricingHop.Address);

// Resilience first, so it sits outermost and the credential handler inside runs once per attempt (§9.7).
pricing
    .AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = PricingHop.TotalRequestTimeout;

        options.Retry.MaxRetryAttempts = PricingHop.MaxRetryAttempts;
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.Delay = PricingHop.RetryDelay;

        options.Retry.MaxDelay = PricingHop.MaxRetryDelay;

        options.AttemptTimeout.Timeout = PricingHop.AttemptTimeout;

        options.CircuitBreaker.FailureRatio = PricingHop.CircuitBreakerFailureRatio;
        options.CircuitBreaker.MinimumThroughput = PricingHop.CircuitBreakerMinimumThroughput;
        options.CircuitBreaker.BreakDuration = PricingHop.CircuitBreakerBreakDuration;

        // SamplingDuration keeps its default, which has to outlive the break duration (§9.7).
    });

// Registered after resilience, so it sits inside it (§11.5).
pricing.AddHttpMessageHandler<ClientCredentialsHandler>();

// §10.4's outbound half: the correlation ID crosses the synchronous hop (§9.7).
builder.Services.AddTransient<CorrelationIdHandler>();
pricing.AddHttpMessageHandler<CorrelationIdHandler>();

// ADR-051's projection and the bus that feeds it; the bus's check joins SQL's in readiness (§13.5).
builder.Services.AddOrderProjection();
builder.Services.AddMassTransitMessaging(builder.Configuration);

// §10.7's read over the projection; a singleton, as the connection factory it holds is (§6.5).
builder.Services.AddSingleton<OrderReader>();

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2); forwarded headers, CORS and the limiter are the edge's.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs

// Above the auth pair, because it converts the bodiless challenge and forbid they write.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4

// SQL and the bus gate readiness (§13.5); Catalog is left out, or its outage would unready this host too.
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapCheckoutEndpoints();
app.MapOrderEndpoints();          // §10.7 — ADR-051's projection, no hop

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory<Program> cannot see (§12.4).
public partial class Program;
