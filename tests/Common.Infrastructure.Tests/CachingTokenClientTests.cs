using Common.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§11.5's token source: what it fetches, what it caches, and what it keeps out of a message.</summary>
public sealed class CachingTokenClientTests : IAsyncLifetime
{
    private const string Scope = "commerce-api";
    private const string AuthorityKey = "Test:Authority";

    private readonly StubIdentityProvider _provider = new();
    private readonly FakeTimeProvider _clock = new();

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _provider.InitializeAsync();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddHttpClient(CachingTokenClient.HttpClientName, c => c.BaseAddress = _provider.Authority);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IOptions<ServiceIdentityOptions>>(
            Options.Create(new ServiceIdentityOptions
            {
                ClientId = "web-bff",
                ClientSecret = "local-dev-secret",
                Scope = Scope
            }));
        services.AddSingleton(new AuthorityKeyName(AuthorityKey));
        services.AddSingleton<ITokenCache, CachingTokenClient>();

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private ITokenCache Tokens => _services.GetRequiredService<ITokenCache>();

    [Fact]
    public async Task It_sends_the_client_credentials_grant()
    {
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        _provider.TokenRequests.TryDequeue(out IReadOnlyDictionary<string, string>? form).ShouldBeTrue();
        form!["grant_type"].ShouldBe("client_credentials");
        form["client_id"].ShouldBe("web-bff");
        form["scope"].ShouldBe(Scope);

        form["client_secret"].ShouldBe("local-dev-secret");
    }

    [Fact]
    public async Task It_reads_the_token_endpoint_from_the_discovery_document()
    {
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        _provider.Discoveries.ShouldBe(1);
    }

    [Fact]
    public async Task The_discovery_document_is_fetched_once_across_many_tokens()
    {
        // Two token fetches, because the clock passes the first token's life between them.
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        _provider.TokenRequests.Count.ShouldBe(2);
        _provider.Discoveries.ShouldBe(1);
    }

    [Fact]
    public async Task A_cached_token_serves_later_calls()
    {
        string first = await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);
        string second = await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        _provider.TokenRequests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_token_inside_the_expiry_guard_is_replaced_before_it_dies()
    {
        _provider.ExpiresIn = 60;

        string first = await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        // 35 s in, 25 s of life remain: inside the 30-second guard, yet still valid.
        _clock.Advance(TimeSpan.FromSeconds(35));

        string second = await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        second.ShouldNotBe(first);
        _provider.TokenRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_response_with_no_expires_in_is_never_cached()
    {
        _provider.ExpiresIn = null;

        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        _provider.TokenRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_callers_cause_one_fetch()
    {
        await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken)));

        _provider.TokenRequests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_refusal_names_the_status_and_the_error_but_never_the_body()
    {
        _provider.TokenStatus = StatusCodes.Status401Unauthorized;
        _provider.TokenFailureBody =
            """{"error":"unauthorized_client","access_token":"leaked-bearer-token"}""";

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("401");
        thrown.Message.ShouldContain("unauthorized_client");

        // §13.4's redactor scrubs keyed attributes and cannot see a token interpolated into a message.
        thrown.Message.ShouldNotContain("leaked-bearer-token");
    }

    [Theory]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    public async Task A_transient_refusal_throws_what_the_resilience_pipeline_retries(int status)
    {
        _provider.TokenStatus = status;

        // The type is the point: §9.7's resilience pipeline retries on what it sees.
        await Should.ThrowAsync<HttpRequestException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_credential_refusal_stays_a_deployment_error(int status)
    {
        _provider.TokenStatus = status;

        await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_blank_access_token_is_refused_rather_than_cached()
    {
        _provider.BlankAccessToken = true;

        await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_discovery_document_with_no_token_endpoint_fails_naming_the_key()
    {
        _provider.OmitTokenEndpoint = true;

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(AuthorityKey);
    }
}
