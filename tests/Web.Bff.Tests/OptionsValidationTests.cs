using Common.Infrastructure.Identity;
using Common.TestSupport;
using Common.Web;
using Microsoft.Extensions.Options;
using Shouldly;
using Web.Bff.Devices;
using Web.Bff.ProductImages;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§15.4's <c>ValidateOnStart</c>: the host refuses to boot without each credential.</summary>
public class OptionsValidationTests
{
    /// <summary>A field, since CA1861 is an error under ADR-019.</summary>
    private static readonly string[] Members = ["ClientId", "ClientSecret", "Scope"];

    /// <summary>The host's own registration refuses the blank credential, and its failure names the member.</summary>
    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void The_host_refuses_to_start_without_each_credential_and_names_it(string member)
    {
        OptionsValidationException refusal =
            Refusal(ServiceIdentityOptions.SectionName, member).ShouldBeOfType<OptionsValidationException>();

        refusal.OptionsType.ShouldBe(typeof(ServiceIdentityOptions));
        refusal.Message.ShouldContain(member);
    }

    /// <summary>ADR-106's store is a start-time requirement too: a host that cannot sign must not boot.</summary>
    [Theory]
    [InlineData("Endpoint")]
    [InlineData("Bucket")]
    [InlineData("Region")]
    [InlineData("AccessKey")]
    [InlineData("SecretKey")]
    public void The_host_refuses_to_start_without_each_store_value_and_names_it(string member)
    {
        OptionsValidationException refusal =
            Refusal(ObjectStoreOptions.SectionName, member).ShouldBeOfType<OptionsValidationException>();

        refusal.OptionsType.ShouldBe(typeof(ObjectStoreOptions));
        refusal.Message.ShouldContain(member);
    }

    /// <summary>ADR-117's app list: a registration cannot be checked against one that names an app
    /// ambiguously.</summary>
    [Theory]
    [InlineData("com.example.shop", "fcm", "com.example.shop", "apns", "2 times")]
    [InlineData("com.example.shop", "fcm", "com.example.shop.ios", "huawei", "not fcm or apns")]
    [InlineData("com.example.shop", "fcm", "com example", "apns", "printable ASCII")]
    public void The_host_refuses_to_start_on_an_app_list_it_cannot_check_against(
        string firstApp,
        string firstPlatform,
        string secondApp,
        string secondPlatform,
        string reason)
    {
        OptionsValidationException refusal = Refusal(
                () => new PushAppsFactory((firstApp, firstPlatform), (secondApp, secondPlatform)))
            .ShouldBeOfType<OptionsValidationException>();

        refusal.OptionsType.ShouldBe(typeof(PushOptions));
        refusal.Message.ShouldContain(reason);
    }

    [Fact]
    public void The_host_starts_with_no_app_to_push_to()
    {
        // A deployment that sends no push is one, so the refusals above are about the list's shape, not its size.
        using PushAppsFactory factory = new();
        using HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    private static Exception Refusal(string section, string member) =>
        Refusal(() => new MissingSettingFactory($"{section}:{member}"));

    // The factory builds the host on first use.
    private static Exception Refusal(Func<BffFactory> unstartable) =>
        StartRefusal.Ask(unstartable, factory => factory.CreateClient());

    [Fact]
    public void The_host_starts_when_all_three_are_supplied()
    {
        // So the refusals cannot pass on a host unstartable for some other reason.
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    /// <summary>The fixture's settings with the app list replaced by the one given.</summary>
    private sealed class PushAppsFactory(params (string AppId, string Platform)[] apps) : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            .. base.Settings.Where(setting =>
                !setting.Key.StartsWith(PushOptions.SectionName + ":", StringComparison.Ordinal)),
            .. apps.SelectMany((app, i) => new KeyValuePair<string, string?>[]
            {
                new($"{PushOptions.SectionName}:Apps:{i}:AppId", app.AppId),
                new($"{PushOptions.SectionName}:Apps:{i}:Platform", app.Platform)
            })
        ];
    }

    /// <summary>One member blanked rather than unset, so an exported environment variable cannot refill it.</summary>
    private sealed class MissingSettingFactory(string blanked) : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
            new("ConnectionStrings:Bff", UnreachableDatabase),
            new("ConnectionStrings:RabbitMq", UnreachableBroker),
            .. StoreSettings.Select(Blank),
            .. Members.Select(name =>
                Blank(new KeyValuePair<string, string?>($"{ServiceIdentityOptions.SectionName}:{name}", "supplied")))
        ];

        private KeyValuePair<string, string?> Blank(KeyValuePair<string, string?> setting) =>
            string.Equals(setting.Key, blanked, StringComparison.Ordinal) ? new(setting.Key, "") : setting;
    }
}
