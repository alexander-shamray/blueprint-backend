using System.Net;
using Grpc.Core;
using Shouldly;
using Web.Bff.TestSupport;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>What §9.7's retry covers on a gRPC client, from both sides: a transport fault, never a status.</summary>
public sealed class UpstreamRetryTests : IAsyncLifetime
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
    public async Task A_transport_fault_is_retried_and_the_request_recovers()
    {
        // Two aborts, then an answer, inside §9.7's three attempts.
        _catalog.AbortNextCalls = 2;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _catalog.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_transport_fault_past_the_budget_exhausts_the_attempts()
    {
        // More aborts than attempts, which pins the attempt count rather than the retrying.
        _catalog.AbortNextCalls = 4;

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // 503 exactly, so the outage mapping is pinned too.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _catalog.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_pipeline_timeout_is_503_rather_than_500()
    {
        // Past the 1.4 s attempt timeout, so Polly's own timeout fires.
        _catalog.HangFor = TimeSpan.FromSeconds(2);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        // Grpc.Net.Client reports a pipeline failure as Internal, with Polly's exception inside.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task An_open_circuit_is_503_without_calling_Catalog_at_all()
    {
        // §9.7's breaker trips at a 0.5 ratio over at least 10 calls, which aborting generously guarantees.
        _catalog.AbortNextCalls = 200;

        using HttpClient client = Caller();

        for (int i = 0; i < 12; i++)
        {
            await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));
        }

        int callsBeforeOpen = _catalog.Calls.Count;

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // Open, it refuses without a call leaving this process.
        _catalog.Calls.Count.ShouldBe(callsBeforeOpen);
    }

    [Fact]
    public async Task A_grpc_status_is_answered_once_and_never_retried()
    {
        // Four refusals queued, of which a pipeline retrying statuses would consume three.
        for (int i = 0; i < 4; i++)
            _catalog.FailNextWith.Enqueue(StatusCode.Unavailable);

        using HttpClient client = Caller();

        HttpResponseMessage response = await client.PostQuote("GBP", TestContext.Current.CancellationToken, (Chair, 1));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        _catalog.Calls.Count.ShouldBe(1);
    }
}
