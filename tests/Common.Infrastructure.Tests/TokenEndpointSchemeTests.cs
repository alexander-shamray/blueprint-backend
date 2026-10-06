using Common.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>Where §11.5's client secret may be posted, whatever the discovery document advertises.</summary>
public sealed class TokenEndpointSchemeTests
{
    private const string Scope = "commerce-api";
    private const string AuthorityKey = "Test:Authority";

    /// <summary>A client over one stub provider, accepting its self-signed certificate.</summary>
    private static ServiceProvider Client(StubIdentityProvider provider)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services
            .AddHttpClient(CachingTokenClient.HttpClientName, c => c.BaseAddress = provider.Authority)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Scoped to this suite's client; the production path validates normally.
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<ServiceIdentityOptions>>(
            Options.Create(new ServiceIdentityOptions
            {
                ClientId = "web-bff",
                ClientSecret = "local-dev-secret",
                Scope = Scope
            }));
        services.AddSingleton(new AuthorityKeyName(AuthorityKey));
        services.AddSingleton<ITokenCache, CachingTokenClient>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_token_endpoint_that_is_not_http_is_refused()
    {
        await using StubIdentityProvider provider = new() { AdvertisedTokenEndpoint = "ftp://example.test/token" };
        await provider.InitializeAsync();

        await using ServiceProvider services = Client(provider);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => services.GetRequiredService<ITokenCache>().GetAsync(Scope, TestContext.Current.CancellationToken));

        // PostAsync would refuse it too, so this guard adds a message, not a protection.
        refusal.Message.ShouldContain("ftp://example.test/token");
    }

    [Fact]
    public async Task An_https_authority_is_not_downgraded_to_plain_http()
    {
        await using StubIdentityProvider provider = new() { UseHttps = true };
        await provider.InitializeAsync();

        provider.Authority.Scheme.ShouldBe("https", "the downgrade is only expressible from an HTTPS authority");
        provider.AdvertisedTokenEndpoint =
            $"http://{provider.Authority.Authority}/realms/test/protocol/openid-connect/token";

        await using ServiceProvider services = Client(provider);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => services.GetRequiredService<ITokenCache>().GetAsync(Scope, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("downgrades the HTTPS authority to plain HTTP");

        // Nothing was posted: a guard after the POST would pass the assertions above and leak.
        provider.TokenRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_https_authority_keeping_https_still_works()
    {
        await using StubIdentityProvider provider = new() { UseHttps = true };
        await provider.InitializeAsync();

        await using ServiceProvider services = Client(provider);

        // The control, without which the refusal above could mean the TLS stub never worked.
        string token = await services
            .GetRequiredService<ITokenCache>()
            .GetAsync(Scope, TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        provider.TokenRequests.Count.ShouldBe(1);
    }
}
