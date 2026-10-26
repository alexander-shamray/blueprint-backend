using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.3's budgets as configuration: the platform's numbers when unset, and honoured when set.</summary>
public sealed partial class GatewayRateLimitOptionsTests(StubDestination stub) : IClassFixture<StubDestination>
{
    private const string PublicRoute = "/api/v1/catalog/products";

    private static ValidateOptionsResult Validate(GatewayRateLimitOptions options) =>
        new GatewayRateLimitOptionsValidator().Validate(null, options);

    [Fact]
    public void An_unset_section_keeps_the_platform_s_budgets()
    {
        // Literals, so a default that moved would fail here and not only in RateLimitedRouteTests' slower drive.
        GatewayRateLimitOptions budgets = new();

        budgets.AnonymousPermitLimit.ShouldBe(100);
        budgets.AnonymousQueueLimit.ShouldBe(0);
        budgets.AuthenticatedTokenLimit.ShouldBe(300);
        budgets.AuthenticatedTokensPerPeriod.ShouldBe(300);
        budgets.AuthenticatedQueueLimit.ShouldBe(10);

        Validate(budgets).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_budget_the_limiter_would_refuse_is_refused_here_first()
    {
        Validate(new GatewayRateLimitOptions { AnonymousPermitLimit = 0 }).Failed.ShouldBeTrue();
        Validate(new GatewayRateLimitOptions { AuthenticatedTokenLimit = 0 }).Failed.ShouldBeTrue();
        Validate(new GatewayRateLimitOptions { AuthenticatedTokensPerPeriod = 0 }).Failed.ShouldBeTrue();
        Validate(new GatewayRateLimitOptions { AnonymousQueueLimit = -1 }).Failed.ShouldBeTrue();
        Validate(new GatewayRateLimitOptions { AuthenticatedQueueLimit = -1 }).Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_configured_anonymous_budget_replaces_the_default_at_the_edge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using BudgetedFactory factory = new(stub.Address, ("RateLimits:AnonymousPermitLimit", "3"));
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < 3; i++)
            (await client.GetAsync(PublicRoute, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync(PublicRoute, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void A_refused_budget_stops_the_gateway_at_start()
    {
        // A failed start can dispose the provider the factory still reads and say only that, so a host that
        // reports the disposal is asked again; Common.TestSupport's StartRefusal does the same for the services.
        Exception? refusal = null;

        for (int attempt = 0; attempt < 5 && refusal is null or ObjectDisposedException; attempt++)
        {
            using BudgetedFactory factory = new(stub.Address, ("RateLimits:AnonymousPermitLimit", "0"));
            refusal = Should.Throw<Exception>(() => factory.CreateClient());
        }

        refusal.ShouldBeOfType<OptionsValidationException>()
            .Message.ShouldContain(nameof(GatewayRateLimitOptions.AnonymousPermitLimit));
    }

    [Fact]
    public void The_stress_override_raises_every_budget_with_values_the_validator_admits()
    {
        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(
                File.ReadAllLines(StressOverride())
                    .Select(line => Entry().Match(line))
                    .Where(entry => entry.Success)
                    .Select(entry => new KeyValuePair<string, string?>(
                        entry.Groups["key"].Value.Replace("__", ":", StringComparison.Ordinal),
                        entry.Groups["value"].Value)))
            .Build();

        GatewayRateLimitOptions stress = settings
            .GetSection(GatewayRateLimitOptions.SectionName)
            .Get<GatewayRateLimitOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
            .ShouldNotBeNull();
        GatewayRateLimitOptions platform = new();

        Validate(stress).Succeeded.ShouldBeTrue();
        stress.AnonymousPermitLimit.ShouldBeGreaterThan(platform.AnonymousPermitLimit);
        stress.AuthenticatedTokenLimit.ShouldBeGreaterThan(platform.AuthenticatedTokenLimit);
        stress.AuthenticatedTokensPerPeriod.ShouldBeGreaterThan(platform.AuthenticatedTokensPerPeriod);
    }

    private static string StressOverride()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return Path.Combine(dir.FullName, "deploy", "compose", "docker-compose.stress.yml");
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex("""^\s+(?<key>[A-Za-z]+__[A-Za-z]+):\s*"?(?<value>[^"#\s]+)"?\s*(?:#.*)?$""")]
    private static partial Regex Entry();

    private sealed class BudgetedFactory(string destination, params (string Key, string Value)[] budgets)
        : StubbedGatewayFactory(destination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
            [.. base.AdditionalSettings, .. budgets.Select(b => new KeyValuePair<string, string>(b.Key, b.Value))];
    }
}
