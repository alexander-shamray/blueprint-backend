using System.Net.Http.Json;
using Shouldly;
using Web.Bff.TestSupport;
using Web.Bff.Endpoints;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§11.5's outbound token and §10.4's correlation ID on the pricing hop.</summary>
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

        // Off the request Catalog received, as a handler registered and never reached is invisible in process.
        _catalog.Calls.Single().Authorization.ShouldBe("Bearer token-1");
    }

    [Fact]
    public async Task The_token_is_minted_for_the_configured_scope()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // The scope becomes the audience every service checks (§11.5).
        _factory.Tokens.Scopes.ShouldBe([BffFactory.Scope]);
    }

    [Fact]
    public async Task The_hop_carries_the_callers_correlation_id()
    {
        using HttpClient client = Caller();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "018f4c2e-supplied");

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // §10.4's propagation across the §9.7 hop, asserted at the receiving end.
        _catalog.Calls.Single().CorrelationId.ShouldBe("018f4c2e-supplied");
    }

    [Fact]
    public async Task A_hop_with_no_inbound_id_carries_a_minted_one()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // The middleware mints an ID when the caller sends none, so the hop carries one and never a blank.
        _catalog.Calls.Single().CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_retried_attempt_asks_the_token_cache_again()
    {
        // A transport fault, as the pipeline retries no gRPC status (§9.7); the cache answers anew each ask.
        _catalog.AbortNextCalls = 1;

        using HttpClient client = Caller();

        QuoteResponse? quote = await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        quote.ShouldNotBeNull();

        string?[] presented = [.. _catalog.Calls.Select(call => call.Authorization)];

        // Inside the resilience pipeline, the credential handler runs once per attempt (§11.5).
        presented.ShouldBe(["Bearer token-1", "Bearer token-2"]);
    }

    [Fact]
    public async Task Two_requests_reuse_the_cache_rather_than_the_token()
    {
        using HttpClient client = Caller();

        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));
        await client.Quote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Two requests are two asks; whether an ask fetches is ITokenCache's concern.
        _factory.Tokens.Issued.ShouldBe(2);
    }
}
