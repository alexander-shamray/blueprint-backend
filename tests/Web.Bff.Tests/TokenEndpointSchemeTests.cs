using Common.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// Where §11.5's client secret is allowed to be posted. The discovery document
/// names the token endpoint, and this is the only thing standing between that
/// name and an outbound credential.
/// </summary>
/// <remarks>
/// <b>The provider is trusted for its answer, not for where its answer
/// points.</b> Reaching the document at all proves the authority responded —
/// over TLS, when the authority is HTTPS — and says nothing about the URL
/// inside it. A document that advertises a plain-HTTP <c>token_endpoint</c>
/// puts <c>ClientSecret</c> on the wire in the clear, and every check before
/// this one has already passed by then.
/// </remarks>
public sealed class TokenEndpointSchemeTests
{
    private const string Scope = "commerce-api";
    private const string AuthorityKey = "Test:Authority";

    /// <summary>
    /// A client over one stub provider, with the self-signed certificate
    /// accepted when the stub is serving TLS.
    /// </summary>
    private static ServiceProvider Client(StubIdentityProvider provider)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services
            .AddHttpClient(CachingTokenClient.HttpClientName, c => c.BaseAddress = provider.Authority)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // The stub's certificate is generated per run and trusted by
                // nothing. Accepting it is scoped to this client in this suite;
                // the production path validates normally, which is the whole
                // premise of the downgrade being worth refusing.
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

        // PostAsync would refuse this one too, with a NotSupportedException
        // naming neither the document nor the configuration key that led here.
        // So the guard is a better message rather than a new protection — the
        // honest half of this pair, and why the test below exists.
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

        // Nothing was posted. The point is not that the call failed — it is
        // that the secret never left, and a guard placed after the POST would
        // satisfy every assertion above while leaking exactly what it refuses.
        provider.TokenRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_https_authority_keeping_https_still_works()
    {
        await using StubIdentityProvider provider = new() { UseHttps = true };
        await provider.InitializeAsync();

        await using ServiceProvider services = Client(provider);

        // The control. Without it the test above could pass because the TLS
        // stub never worked at all — a refusal for the right reason and a
        // refusal for no reason are the same assertion from outside.
        string token = await services.GetRequiredService<ITokenCache>()
            .GetAsync(Scope, TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        provider.TokenRequests.Count.ShouldBe(1);
    }
}
