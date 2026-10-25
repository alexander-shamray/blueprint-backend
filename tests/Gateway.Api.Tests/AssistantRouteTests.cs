using System.Net;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>Which BFF release a path reaches, each release behind a stub of its own (ADR-153).</summary>
public sealed class AssistantRouteTests(StubDestination bff) : IClassFixture<StubDestination>, IAsyncLifetime
{
    private readonly StubDestination _assistant = new();

    public ValueTask InitializeAsync() => _assistant.InitializeAsync();

    public ValueTask DisposeAsync() => _assistant.DisposeAsync();

    [Theory]
    [InlineData("/bff/v1/assistant", "/v1/assistant")]
    [InlineData("/bff/v1/assistant/", "/v1/assistant/")]
    [InlineData("/bff/v1/assistant/conversations", "/v1/assistant/conversations")]
    public async Task An_assistant_path_reaches_the_assistant_release_and_not_the_main_one(
        string path,
        string forwarded)
    {
        using SplitBffGatewayFactory factory = new(bff.Address, _assistant.Address);
        using HttpClient client = factory.CreateClient();
        int before = bff.ReceivedPaths.Count;

        using HttpRequestMessage request = Get(path);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _assistant.ReceivedPaths.Last().ShouldBe(forwarded);
        bff.ReceivedPaths.Count.ShouldBe(before, "the main release holds no conversation (ADR-153)");
    }

    /// <summary>The other direction, including a path that only starts with the assistant's segment.</summary>
    [Theory]
    [InlineData("/bff/v1/orders", "/v1/orders")]
    [InlineData("/bff/v1/assistants", "/v1/assistants")]
    public async Task Every_other_bff_path_reaches_the_main_release(string path, string forwarded)
    {
        using SplitBffGatewayFactory factory = new(bff.Address, _assistant.Address);
        using HttpClient client = factory.CreateClient();
        int before = _assistant.ReceivedPaths.Count;

        using HttpRequestMessage request = Get(path);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        bff.ReceivedPaths.Last().ShouldBe(forwarded);
        _assistant.ReceivedPaths.Count.ShouldBe(before);
    }

    [Fact]
    public async Task An_anonymous_caller_reaches_neither_release()
    {
        using SplitBffGatewayFactory factory = new(bff.Address, _assistant.Address);
        using HttpClient client = factory.CreateClient();
        int before = _assistant.ReceivedPaths.Count;

        HttpResponseMessage response =
            await client.GetAsync("/bff/v1/assistant/conversations", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _assistant.ReceivedPaths.Count.ShouldBe(before);
    }

    private static HttpRequestMessage Get(string path)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        return request;
    }

    /// <summary>Every cluster at the main stub but the assistant's, which the later setting overrides.</summary>
    private sealed class SplitBffGatewayFactory(string bffDestination, string assistantDestination)
        : StubbedGatewayFactory(bffDestination)
    {
        protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
        [
            .. base.AdditionalSettings,
            new("ReverseProxy:Clusters:web-bff-assistant:Destinations:d1:Address", assistantDestination)
        ];
    }
}
