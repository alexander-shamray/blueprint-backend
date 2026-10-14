using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.Push;

namespace Notifications.Infrastructure.Push;

/// <summary>The providers' registration, beside the layer's, as its rule needs the host (ADR-055).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddPushChannel(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Read before the host is built, as the provider a platform needs is registered only when an app names it.
        PushOptions apps = configuration.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new PushOptions();

        // Validated at start beside its consumer (§15.4): IOptions<T> always resolves, so a forgotten bind is silent.
        services
            .AddOptions<PushOptions>()
            .BindConfiguration(PushOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PushOptions>, PushOptionsValidator>();
        services.AddSingleton<PushMetrics>();

        if (apps.Names(FcmPushProvider.Name))
            AddFcm(services, configuration, environment);

        if (apps.Names(ApnsPushProvider.Name))
            AddApns(services, configuration, environment);

        // A singleton, as it holds no state of its own and every provider it routes to is one too.
        services.AddSingleton<IPushChannel, PushRouter>();

        return services;
    }

    private static void AddFcm(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            FcmOptions? fcm = configuration.GetSection(FcmOptions.SectionName).Get<FcmOptions>();
            RefusePlain(fcm?.BaseUrl, FcmOptions.BaseUrlKey);
            RefusePlain(fcm?.TokenUri, FcmOptions.TokenUriKey);
            RefuseNoKey(fcm?.PrivateKey, FcmOptions.PrivateKeyKey);
        }

        services
            .AddOptions<FcmOptions>()
            .BindConfiguration(FcmOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FcmOptions>, FcmOptionsValidator>();

        AddClient(services, FcmPushProvider.ClientName);

        // Singletons, so the cached token and the breaker are the host's and not a scope's.
        services.AddSingleton<FcmAccessTokens>();
        services.AddSingleton<IPushProvider, FcmPushProvider>();
    }

    private static void AddApns(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            ApnsOptions? apns = configuration.GetSection(ApnsOptions.SectionName).Get<ApnsOptions>();
            RefusePlain(apns?.BaseUrl, ApnsOptions.BaseUrlKey);
            RefuseNoKey(apns?.PrivateKey, ApnsOptions.PrivateKeyKey);
        }

        services
            .AddOptions<ApnsOptions>()
            .BindConfiguration(ApnsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApnsOptions>, ApnsOptionsValidator>();

        AddClient(services, ApnsPushProvider.ClientName);

        // Singletons, so the cached token and the breaker are the host's and not a scope's.
        services.AddSingleton<ApnsProviderTokens>();
        services.AddSingleton<IPushProvider, ApnsPushProvider>();
    }

    // No logger, as the factory's logs the request's address and APNs' holds the token (ADR-117). The pipeline
    // owns every timeout, and a followed redirect would replay a push to wherever it pointed.
    private static void AddClient(IServiceCollection services, string name) =>
        services
            .AddHttpClient(name, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                EnableMultipleHttp2Connections = true
            });

    // No message echoes a configured value, as a failed start is logged and the key sits beside the rest.
    private static void RefusePlain(Uri? url, string key)
    {
        if (url is not null && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{key} is not HTTPS outside Development; the provider's bearer token would travel in the clear.");
        }
    }

    private static void RefuseNoKey(string? key, string name)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"{name} is required outside Development; a key is generated in Development alone.");
        }
    }
}
