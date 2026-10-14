using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.Push;
using Notifications.Infrastructure.Push;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The registration refuses to send in the clear or unsigned, and registers what an app names.</summary>
public sealed class PushOptionsTests
{
    private const string Provider = "https://push.example.test/";

    [Theory]
    [InlineData(FcmOptions.BaseUrlKey)]
    [InlineData(FcmOptions.TokenUriKey)]
    [InlineData(ApnsOptions.BaseUrlKey)]
    public void Outside_development_plain_http_is_refused_at_registration(string key)
    {
        Dictionary<string, string?> settings = Production();
        settings[key] = "http://push.example.test/";

        Should
            .Throw<InvalidOperationException>(() => PushSettings.Registered(Environments.Production, settings))
            .Message.ShouldContain($"{key} is not HTTPS outside Development");
    }

    [Theory]
    [InlineData(FcmOptions.PrivateKeyKey)]
    [InlineData(ApnsOptions.PrivateKeyKey)]
    public void Outside_development_a_missing_key_is_refused_at_registration(string key)
    {
        Dictionary<string, string?> settings = Production();
        settings[key] = null;

        Should
            .Throw<InvalidOperationException>(() => PushSettings.Registered(Environments.Production, settings))
            .Message.ShouldContain($"{key} is required outside Development");
    }

    [Fact]
    public void Outside_development_https_and_both_keys_start_and_build_both_pipelines()
    {
        // The control for the refusals above, so they cannot pass against a registration nothing satisfies; resolving
        // the channel builds each provider's pipeline, whose options Polly validates in Build().
        using ServiceProvider provider = PushSettings.Registered(Environments.Production, Production())
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Should.NotThrow(() => provider.GetRequiredService<IPushChannel>());
        provider.GetServices<IPushProvider>().Select(p => p.Platform).ShouldBe(["fcm", "apns"], ignoreOrder: true);
    }

    [Fact]
    public void In_development_plain_http_and_no_key_start()
    {
        using ServiceProvider provider = PushSettings
            .Registered(Environments.Development, PushSettings.Both("http://push-simulator/"))
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_platform_no_app_names_has_no_provider_and_needs_no_options()
    {
        Dictionary<string, string?> settings = new()
        {
            [$"{PushOptions.AppsKey}:0:AppId"] = PushSettings.ApnsApp,
            [$"{PushOptions.AppsKey}:0:Platform"] = ApnsPushProvider.Name,
            [ApnsOptions.BaseUrlKey] = Provider,
            [ApnsOptions.TeamIdKey] = "TESTTEAM01",
            [ApnsOptions.KeyIdKey] = "TESTKEY001",
            [ApnsOptions.PrivateKeyKey] = EcKey()
        };

        using ServiceProvider provider = PushSettings.Registered(Environments.Production, settings)
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetServices<IPushProvider>().Select(p => p.Platform).ShouldBe(["apns"]);
    }

    [Fact]
    public void No_app_at_all_starts_with_a_channel_and_no_provider()
    {
        using ServiceProvider provider = PushSettings.Registered(Environments.Production, [])
            .BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
        provider.GetServices<IPushProvider>().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0:Platform", "gcm", "is not fcm or apns")]
    [InlineData("1:AppId", PushSettings.FcmApp, "names an app already named")]
    [InlineData("0:AppId", "com.example shop", "is not an app id")]
    public void An_app_list_the_bff_would_refuse_stops_the_host(string member, string value, string message)
    {
        Dictionary<string, string?> settings = PushSettings.Both("http://push-simulator/");
        settings[$"{PushOptions.AppsKey}:{member}"] = value;

        using ServiceProvider provider = PushSettings.Registered(Environments.Development, settings)
            .BuildServiceProvider();

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain(message);
    }

    [Theory]
    [InlineData(FcmOptions.PrivateKeyKey, "is not an RSA key in PEM")]
    [InlineData(ApnsOptions.PrivateKeyKey, "is not an EC key in PEM")]
    public void A_key_that_is_not_pem_stops_the_host(string key, string message)
    {
        Dictionary<string, string?> settings = PushSettings.Both("http://push-simulator/");
        settings[key] = "not-a-key";

        using ServiceProvider provider = PushSettings.Registered(Environments.Development, settings)
            .BuildServiceProvider();

        Should
            .Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain(message);
    }

    [Fact]
    public void An_address_carrying_a_credential_stops_the_host()
    {
        Dictionary<string, string?> settings = PushSettings.Both("http://push-simulator/");
        settings[FcmOptions.BaseUrlKey] = "http://user:secret@push-simulator/";

        using ServiceProvider provider = PushSettings.Registered(Environments.Development, settings)
            .BuildServiceProvider();

        OptionsValidationException refused = Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());

        refused.Message.ShouldContain($"{FcmOptions.BaseUrlKey} carries a credential");
        refused.Message.ShouldNotContain("secret", Case.Sensitive, "no message echoes a configured value");
    }

    private static Dictionary<string, string?> Production()
    {
        Dictionary<string, string?> settings = PushSettings.Both(Provider);
        settings[FcmOptions.PrivateKeyKey] = RsaKey();
        settings[ApnsOptions.PrivateKeyKey] = EcKey();
        return settings;
    }

    private static string RsaKey()
    {
        using RSA key = RSA.Create(2048);
        return key.ExportPkcs8PrivateKeyPem();
    }

    private static string EcKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
