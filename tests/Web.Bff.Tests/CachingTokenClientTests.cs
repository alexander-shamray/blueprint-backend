using Common.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §11.5's token source: what it fetches, what it caches, and what it refuses
/// to put in an exception message.
/// </summary>
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

        // In the body rather than a basic-auth header — both are permitted by
        // RFC 6749 §2.3.1, and the body form keeps the secret out of the one
        // header every proxy in the world is willing to log.
        form["client_secret"].ShouldBe("local-dev-secret");
    }

    [Fact]
    public async Task It_reads_the_token_endpoint_from_the_discovery_document()
    {
        await Tokens.GetAsync(Scope, TestContext.Current.CancellationToken);

        // Fetched, rather than built by appending a provider-shaped path to
        // the authority. The stub's token endpoint is only reachable through
        // the document, so a client that guessed the path would 404.
        _provider.Discoveries.ShouldBe(1);
    }

    [Fact]
    public async Task The_discovery_document_is_fetched_once_across_many_tokens()
    {
        // Two token fetches, because the clock is advanced past the first
        // token's life between them.
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

        // Thirty-five seconds in: the token has twenty-five seconds of life
        // left, which is inside the thirty-second guard and still valid. The
        // guard's whole reason is that a token handed out here would expire
        // DURING the five seconds of retries §9.7 permits — so the assertion
        // is that it is replaced while it is still good, not after it is dead.
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

        // expires_in is OPTIONAL in RFC 6749 §5.1. Absent, the safe reading is
        // "already expired" — one fetch per call, which is wasteful and
        // correct. The unsafe reading is a default hour, which attaches a dead
        // token for fifty-nine minutes of it.
        _provider.TokenRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_callers_cause_one_fetch()
    {
        await Task.WhenAll(Enumerable
            .Range(0, 20)
            .Select(_ => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken)));

        // The gate plus the re-read inside it. Without the second read every
        // waiter fetches a token for a value already in hand, which is a
        // twenty-fold burst at the provider on the first request after a
        // restart — exactly when it is least welcome.
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

        // The half that matters. A token endpoint answers a SUCCESSFUL grant
        // with a bearer token, so a failure path that echoes whatever arrived
        // is one provider quirk away from writing that token into a log —
        // where §13.4's redactor cannot reach it, because it scrubs keyed
        // attributes and cannot see a secret interpolated into a message.
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

        // HttpRequestException, not InvalidOperationException — and the type is
        // the whole point rather than a detail. This runs inside the pricing
        // client's resilience pipeline (§9.7), which decides what to retry from
        // the exception it sees: an InvalidOperationException is not transient
        // to it, so a Keycloak that was merely restarting used to take the
        // request down as an unmapped 500.
        await Should.ThrowAsync<HttpRequestException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_credential_refusal_stays_a_deployment_error(int status)
    {
        _provider.TokenStatus = status;

        // The other half, and it must not become retryable: a wrong client
        // secret retried three times is three ways of being wrong, and the
        // failure a deployment needs to see is the first one.
        await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_blank_access_token_is_refused_rather_than_cached()
    {
        _provider.BlankAccessToken = true;

        // Blank counts as missing — the same lesson §11.3 records for
        // Identity:Authority. A present-but-empty token passes a type check and
        // is then CACHED for expires_in, so every pricing call sends `Bearer `
        // for minutes, and the resulting 401s look like the audience mapper
        // rather than like the provider.
        await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_discovery_document_with_no_token_endpoint_fails_naming_the_key()
    {
        _provider.OmitTokenEndpoint = true;

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Tokens.GetAsync(Scope, TestContext.Current.CancellationToken));

        // The key the host was configured from is what turns this from
        // "something went wrong talking to the provider" into a deployment
        // instruction, so the name this client was given is what it prints.
        thrown.Message.ShouldContain(AuthorityKey);
    }
}
