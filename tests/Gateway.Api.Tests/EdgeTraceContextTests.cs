using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>A caller's W3C trace context stops at the edge, so it cannot choose its work's trace (ADR-083).</summary>
public sealed class EdgeTraceContextTests(StubDestination stub) : IClassFixture<StubDestination>
{
    private const string ClientTraceId = "0af7651916cd43dd8448eb211c80319c";

    [Fact]
    public async Task A_client_traceparent_tracestate_and_baggage_do_not_reach_the_service()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/products");
        request.Headers.Add("traceparent", $"00-{ClientTraceId}-b7ad6b7169203331-01");
        request.Headers.Add("tracestate", "client=chosen");
        request.Headers.Add("baggage", "tenant=chosen");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        StubDestination.TraceHeaders received = stub.ReceivedTraceHeaders.Last();

        // The gateway's own context goes on: a trace the edge started, not one the caller named.
        received.TraceParent.ShouldNotBeNull("the edge propagates its own trace downstream");
        received.TraceParent.ShouldNotContain(ClientTraceId);
        received.TraceState.ShouldBeNull();
        received.Baggage.ShouldBeNull();
    }
}
