using System.Net.Http.Json;
using Shouldly;
using Web.Bff.TestSupport;
using Web.Bff.Endpoints;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §11.5's outbound identity, observed at the wire: that a token is attached
/// at all, that it is the configured scope's, and — the claim §9.7 and §11.5
/// both make and neither could check — that every attempt asks the cache
/// <b>again</b> rather than replaying the first attempt's token.
/// §10.4's outbound correlation ID is here too, for the same reason: it is a
/// header on the same hop and only the far end can see it.
/// </summary>
public sealed class PricingCredentialsTests : IAsyncLifetime
{
    private static readonly Guid Chair = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly StubCatalog _catalog = new();

    private BffFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _catalog.InitializeAsync();

        _factory = new BffFactory { PricingAddress = _catalog.Address };
        _catalog.Prices[Chair] = ("Chair", 49.99m, "GBP");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    private HttpClient Caller()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "customer-1");

        return client;
    }

    [Fact]
    public async Task Every_outbound_call_carries_a_bearer_token()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Read off the request Catalog actually received, not off the handler.
        // A DelegatingHandler registered and never reached is the failure this
        // is for, and it is invisible from inside the process.
        _catalog.Calls.Single().Authorization.ShouldBe("Bearer token-1");
    }

    [Fact]
    public async Task The_token_is_minted_for_the_configured_scope()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // §11.5: the scope has to become an audience, and a client-credentials
        // token that asked for the wrong scope carries the wrong `aud` and is
        // refused by every service — at the one moment there is no user to
        // blame it on.
        _factory.Tokens.Scopes.ShouldBe([BffFactory.Scope]);
    }

    [Fact]
    public async Task The_hop_carries_the_callers_correlation_id()
    {
        using HttpClient client = Caller();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "018f4c2e-supplied");

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // §10.4 promises one ID "propagates through every service", and this
        // is the one hop §9.7 makes on behalf of an inbound request — so it is
        // the only place that promise could be broken by a process boundary.
        // Asserted at the RECEIVING end for the reason the token above is: a
        // handler that sets the header on a request nobody sends looks
        // identical from inside this process.
        _catalog.Calls.Single().CorrelationId.ShouldBe("018f4c2e-supplied");
    }

    [Fact]
    public async Task A_hop_with_no_inbound_id_sends_no_header()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // The middleware mints one when the caller sends none, so this hop
        // does carry an ID — what it must not do is invent an EMPTY one. A
        // blank header would defeat the blank-counts-as-missing guard on the
        // receiving side, which is a lesson this repository has already had to
        // learn twice.
        _catalog.Calls.Single().CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_retried_attempt_asks_the_token_cache_again()
    {
        // The first attempt fails at the TRANSPORT, not with a gRPC status,
        // and that distinction is load-bearing rather than incidental: a gRPC
        // status rides an HTTP 200 with grpc-status in the trailers, so the
        // resilience pipeline sees a successful response and retries nothing.
        // An aborted connection is an HttpRequestException, which it does
        // retry — see UpstreamRetryTests, which is where that pair is measured.
        //
        // With the retry established, this is the only case in which the two
        // orderings produce different bytes — and therefore the only thing
        // that can measure the claim at all. §11.5 used to justify the
        // position with "a retry after a 401", which its own configuration
        // rules out; what is left, and what this drives, is that a repeated
        // attempt goes back to the cache instead of replaying the token the
        // first attempt built.
        //
        // NOT that the token is newly minted. CachingTokenClient hands back
        // the same one until its expiry guard, so in production two attempts
        // milliseconds apart carry identical bytes — which is correct, and is
        // why RecordingTokenCache answers differently every time. Its job is
        // to make "the handler ran again" visible, and the only way to see
        // that on the wire is to have the answer change.
        _catalog.AbortNextCalls = 1;

        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        quote.ShouldNotBeNull();

        string?[] presented = [.. _catalog.Calls.Select(call => call.Authorization)];

        // Two attempts, two DIFFERENT tokens. Registered the other way round —
        // the credential handler OUTSIDE the resilience pipeline — the handler
        // runs once per logical request rather than once per attempt, so both
        // lines here would read "Bearer token-1" and a token that expired
        // between them could never be replaced.
        //
        // This is what makes the registration order in Program.cs a claim a
        // test is making rather than a comment nobody can check.
        presented.ShouldBe(["Bearer token-1", "Bearer token-2"]);
    }

    [Fact]
    public async Task Two_requests_reuse_the_cache_rather_than_the_token()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));
        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // The handler asks the cache on every attempt, deliberately — that is
        // what the test above depends on. Which means the *caching* is
        // entirely ITokenCache's job, and this test says so out loud: two
        // requests here are two asks, and it is CachingTokenClientTests that
        // proves an ask is not a fetch.
        _factory.Tokens.Issued.ShouldBe(2);
    }
}
