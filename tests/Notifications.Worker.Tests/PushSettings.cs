using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notifications.Application.Push;
using Notifications.Infrastructure.Push;

namespace Notifications.Worker.Tests;

/// <summary>The push channel's registration over the simulator, and a push to send through it.</summary>
internal static class PushSettings
{
    public const string FcmApp = "com.example.shop";

    public const string ApnsApp = "com.example.shop.ios";

    public static Dictionary<string, string?> Both(string baseUrl) =>
        new()
        {
            [$"{PushOptions.AppsKey}:0:AppId"] = FcmApp,
            [$"{PushOptions.AppsKey}:0:Platform"] = FcmPushProvider.Name,
            [$"{PushOptions.AppsKey}:1:AppId"] = ApnsApp,
            [$"{PushOptions.AppsKey}:1:Platform"] = ApnsPushProvider.Name,
            [FcmOptions.BaseUrlKey] = baseUrl,
            [FcmOptions.TokenUriKey] = baseUrl + "token",
            [FcmOptions.ProjectIdKey] = "commerce-test",
            [FcmOptions.ClientEmailKey] = "notifications@commerce-test.example.invalid",
            [ApnsOptions.BaseUrlKey] = baseUrl,
            [ApnsOptions.TeamIdKey] = "TESTTEAM01",
            [ApnsOptions.KeyIdKey] = "TESTKEY001"
        };

    /// <summary>The production registration over an environment, logging to <paramref name="logs"/>.</summary>
    public static ServiceCollection Registered(
        string environment,
        Dictionary<string, string?> settings,
        TimeProvider? clock = null,
        ILoggerProvider? logs = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddMetrics();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
                logging.AddProvider(logs);
        });
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddPushChannel(configuration, new TestEnvironment { EnvironmentName = environment });

        return services;
    }

    public static OutboundPush To(string platform, string token) =>
        new(
            platform,
            platform == FcmPushProvider.Name ? FcmApp : ApnsApp,
            token,
            "Your order is placed",
            "Order 42 is placed.",
            Guid.CreateVersion7(),
            "order-placed",
            Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddDays(1));
}
