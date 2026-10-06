using System.Net.Http.Headers;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Application.Contacts;
using Polly;

namespace Notifications.Infrastructure.Contacts;

/// <summary>ADR-052's contact read, in a method of its own as ADR-055 places a hop; the host calls it.</summary>
public static class DependencyInjection
{
    private const string Section = "ContactSource";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string RealmKey = $"{Section}:Realm";

    public static IServiceCollection AddContactSource(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        Uri baseUrl = BaseUrl(configuration, environment);

        // Every request is relative to one realm's admin path, built and checked once here.
        Uri admin = new(baseUrl, $"admin/realms/{Realm(configuration)}/");

        services.AddSingleton<ContactMetrics>();
        services.AddTransient<ContactAnswerBuffer>();

        IHttpClientBuilder client = services.AddHttpClient<IContactSource, KeycloakContactSource>(
            ContactHop.ClientName,
            http =>
            {
                http.BaseAddress = admin;
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // A followed redirect would carry a token that reads every user wherever it pointed (ADR-052).
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Resilience first, so it is outermost and a retried attempt asks for a token again (§9.7, §11.5).
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = ContactHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = ContactHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = ContactHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = ContactHop.RetryDelay;
            options.Retry.MaxDelay = ContactHop.MaxRetryDelay;

            // MaxDelay does not cap a Retry-After, so one long header would spend the budget ContactHop counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            options.CircuitBreaker.FailureRatio = ContactHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = ContactHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = ContactHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = ContactHop.CircuitBreakerBreakDuration;
        });

        client.AddHttpMessageHandler<ClientCredentialsHandler>();

        // Innermost, so a body that stalls or runs over is an attempt the pipeline bounds and retries.
        client.AddHttpMessageHandler<ContactAnswerBuffer>();

        return services;
    }

    // No message echoes the configured value, since an address can carry user information.
    private static Uri BaseUrl(IConfiguration configuration, IHostEnvironment environment)
    {
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} is not configured. Notifications cannot read a contact.");
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; this host authenticates with §11.5's grant alone.");
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries a query or fragment, which no request to Keycloak would keep.");
        }

        // HTTPS outside Development, as for the authority (§11.3): the token reads every user, the answer is personal.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "a token that reads every user would travel in the clear.");
        }

        // A trailing slash, or a relative request would replace the base address's last segment.
        return parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
    }

    // One path segment of plain characters, since the name is written into every request's path.
    private static string Realm(IConfiguration configuration)
    {
        string? realm = configuration[RealmKey];
        if (string.IsNullOrWhiteSpace(realm))
        {
            throw new InvalidOperationException(
                $"{RealmKey} is not configured. Notifications cannot name the realm whose users it reads.");
        }

        bool segment = realm is not ("." or "..") &&
            realm.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        return segment
            ? realm
            : throw new InvalidOperationException($"{RealmKey} is not a realm name this host can put in a path.");
    }
}
