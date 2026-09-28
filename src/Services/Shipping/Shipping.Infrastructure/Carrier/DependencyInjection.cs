using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The carrier's registration, apart from <c>AddShippingInfrastructure</c>
/// because its scheme rule needs the host's environment, which that method is
/// not given.
/// </summary>
public static class DependencyInjection
{
    // The section is written once, so the two setting names cannot name
    // different sections; ApiKeyKey is the setting's name, never its value.
    private const string Section = "Carrier";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string ApiKeyKey = $"{Section}:ApiKey";

    public static IServiceCollection AddCarrierGateway(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        Uri parsed = ConfiguredBaseUrl.Read(
            configuration,
            BaseUrlKey,
            whenMissing: "Shipping cannot reach a carrier.",
            whenUserInfo: $"the carrier's credential is {ApiKeyKey} alone.",
            peer: "the carrier");

        // HTTPS everywhere but Development, the rule AuthenticationExtensions
        // applies to the identity provider: the key below is a bearer
        // credential, and plain HTTP hands it to anyone on the path. The local
        // simulator is Development's, and the one plain-HTTP carrier there is.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "the carrier key would travel in the clear.");
        }

        // A trailing slash, always: without one a relative request replaces
        // the base address's last segment, so a carrier at …/api would be
        // called at …/v1/shipments.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        // Required for the same reason, and §15.4 says so: a host must not
        // start and then call a carrier unauthenticated.
        string? apiKey = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{ApiKeyKey} is not configured. Shipping does not call a carrier unauthenticated.");
        }

        services.AddSingleton<CarrierMetrics>();
        services.AddTransient<CarrierAttemptCounter>();
        services.AddTransient<CarrierAnswerBuffer>();

        IHttpClientBuilder client = services.AddHttpClient<ICarrierGateway, HttpCarrierGateway>(http =>
        {
            http.BaseAddress = baseAddress;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        });

        // A followed 307 or 308 would replay the address to wherever the
        // carrier pointed, and take that answer as its booking. Unfollowed, a
        // redirect is a status the adapter does not define.
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Separate statements: AddStandardResilienceHandler returns the
        // pipeline's builder, not the client's, so a chained
        // AddHttpMessageHandler would not compile onto the client. Added after
        // the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = CarrierHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = CarrierHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = CarrierHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = CarrierHop.RetryDelay;
            options.Retry.MaxDelay = CarrierHop.MaxRetryDelay;

            // A Retry-After replaces the backoff above and MaxDelay does not
            // cap it, so one long header would spend the total before the
            // retry CarrierHop's budget counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            // The endpoint defaults never open for a loop that makes a handful
            // of calls a minute, which is what CarrierHop's own summary argues.
            options.CircuitBreaker.FailureRatio = CarrierHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = CarrierHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = CarrierHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = CarrierHop.CircuitBreakerBreakDuration;

            // An attempt timeout is the carrier's, and this is the one place
            // it arrives distinguishable from the caller cancelling.
            CarrierMetrics metrics = sp.GetRequiredService<CarrierMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<CarrierAttemptCounter>();

        // Inside the counter, so a body that breaks off or runs over is an
        // attempt it counts and the pipeline retries.
        client.AddHttpMessageHandler<CarrierAnswerBuffer>();

        return services;
    }
}
