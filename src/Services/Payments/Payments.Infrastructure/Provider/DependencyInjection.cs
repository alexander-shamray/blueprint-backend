using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Payments.Application.Provider;
using Polly;

namespace Payments.Infrastructure.Provider;

/// <summary>
/// The provider's registration, apart from <c>AddPaymentsInfrastructure</c>
/// because its scheme rule needs the host's environment, which that method is
/// not given.
/// </summary>
public static class DependencyInjection
{
    // The section is written once, so the two setting names cannot name
    // different sections; ApiKeyKey is the setting's name, never its value.
    private const string Section = "PaymentProvider";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string ApiKeyKey = $"{Section}:ApiKey";

    public static IServiceCollection AddPaymentProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Eager, as the broker's key is: a host that cannot name its provider
        // does not start, rather than failing its first authorisation.
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{BaseUrlKey} is not configured. Payments cannot reach a provider.");

        // No message below echoes the configured value: a startup failure is
        // logged, and an address can carry user information.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        // The provider is authenticated by the key alone, and a credential in
        // the address would travel wherever the address is printed.
        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; the provider's credential is {ApiKeyKey} alone.");
        }

        // HTTPS everywhere but Development, the rule AuthenticationExtensions
        // applies to the identity provider: the key below is a bearer
        // credential, and plain HTTP hands it to anyone on the path. The local
        // simulator is Development's, and the one plain-HTTP provider there is.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "the provider key would travel in the clear.");
        }

        // A trailing slash, always: without one a relative request replaces
        // the base address's last segment, so a provider at …/api would be
        // called at …/v1/authorisations.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        // Required for the same reason, and §15.4 says so: a host must not
        // start and then call a provider unauthenticated.
        string? apiKey = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{ApiKeyKey} is not configured. Payments does not call a provider unauthenticated.");
        }

        services.AddSingleton<ProviderMetrics>();
        services.AddTransient<ProviderAttemptCounter>();

        IHttpClientBuilder client = services.AddHttpClient<IPaymentProvider, HttpPaymentProvider>(http =>
        {
            http.BaseAddress = baseAddress;
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        });

        // Separate statements: AddStandardResilienceHandler returns the
        // pipeline's builder, not the client's, so a chained
        // AddHttpMessageHandler would not compile onto the client. Added after
        // the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = ProviderHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = ProviderHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = ProviderHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = ProviderHop.RetryDelay;
            options.Retry.MaxDelay = ProviderHop.MaxRetryDelay;

            // An attempt timeout is the provider's, and this is the one place
            // it arrives distinguishable from the caller cancelling.
            ProviderMetrics metrics = sp.GetRequiredService<ProviderMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<ProviderAttemptCounter>();

        return services;
    }
}
