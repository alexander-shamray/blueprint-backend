using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Carrier;

/// <summary>Apart from <c>AddShippingInfrastructure</c>: the scheme rule needs the host's environment.</summary>
public static class DependencyInjection
{
    // ApiKeyKey is the setting's name, never its value.
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

        // HTTPS outside Development, as for the identity provider: the key is a bearer credential.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "the carrier key would travel in the clear.");
        }

        // A trailing slash, or a relative request would replace the base address's last segment.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        // Required (§15.4): a host must not start and then call a carrier unauthenticated.
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

        // A followed redirect would replay the address wherever it pointed and take that answer as the booking.
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Added after the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = CarrierHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = CarrierHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = CarrierHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = CarrierHop.RetryDelay;
            options.Retry.MaxDelay = CarrierHop.MaxRetryDelay;

            // MaxDelay does not cap a Retry-After, so one long header would spend the budget CarrierHop counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            options.CircuitBreaker.FailureRatio = CarrierHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = CarrierHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = CarrierHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = CarrierHop.CircuitBreakerBreakDuration;

            // The one place an attempt timeout is distinguishable from the caller cancelling.
            CarrierMetrics metrics = sp.GetRequiredService<CarrierMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<CarrierAttemptCounter>();

        // Inside the counter, so a body that breaks off or runs over is a counted, retried attempt.
        client.AddHttpMessageHandler<CarrierAnswerBuffer>();

        return services;
    }
}
