using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Payments.Application.Provider;
using Polly;

namespace Payments.Infrastructure.Provider;

/// <summary>Apart from <c>AddPaymentsInfrastructure</c>: the scheme rule needs the host's environment.</summary>
public static class DependencyInjection
{
    // ApiKeyKey is the setting's name, never its value.
    private const string Section = "PaymentProvider";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string ApiKeyKey = $"{Section}:ApiKey";

    public static IServiceCollection AddPaymentProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Eager: a host that cannot name its provider does not start.
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{BaseUrlKey} is not configured. Payments cannot reach a provider.");

        // No message echoes the configured value, since an address can carry user information.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        // The key is the credential; one in the address would travel wherever the address is printed.
        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; the provider's credential is {ApiKeyKey} alone.");
        }

        // A relative request keeps the address's path but drops its query and fragment.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries a query or fragment, which no request to the provider would keep.");
        }

        // HTTPS outside Development, as for the identity provider: the key is a bearer credential.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "the provider key would travel in the clear.");
        }

        // A trailing slash, or a relative request would replace the base address's last segment.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        // Required (§15.4): a host must not start and then call a provider unauthenticated.
        string? apiKey = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{ApiKeyKey} is not configured. Payments does not call a provider unauthenticated.");
        }

        services.AddSingleton<ProviderMetrics>();
        services.AddTransient<ProviderAttemptCounter>();
        services.AddTransient<ProviderAnswerBuffer>();

        IHttpClientBuilder client = services.AddHttpClient<IPaymentProvider, HttpPaymentProvider>(http =>
        {
            http.BaseAddress = baseAddress;
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        });

        // A followed redirect would replay the payer and amount wherever it pointed and take that as the verdict.
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Added after the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = ProviderHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = ProviderHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = ProviderHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = ProviderHop.RetryDelay;
            options.Retry.MaxDelay = ProviderHop.MaxRetryDelay;

            options.CircuitBreaker.FailureRatio = ProviderHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = ProviderHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = ProviderHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = ProviderHop.CircuitBreakerBreakDuration;

            // MaxDelay does not cap a Retry-After, so one long header would spend the budget ProviderHop counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            // The one place an attempt timeout is distinguishable from the caller cancelling.
            ProviderMetrics metrics = sp.GetRequiredService<ProviderMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<ProviderAttemptCounter>();

        // Inside the counter, so a body that breaks off or runs over is a counted, retried attempt.
        client.AddHttpMessageHandler<ProviderAnswerBuffer>();

        return services;
    }
}
