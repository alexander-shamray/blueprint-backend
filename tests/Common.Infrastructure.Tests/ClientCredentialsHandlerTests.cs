using System.Net;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§11.5's handler: the token it attaches, and the refusals that evict it.</summary>
public sealed class ClientCredentialsHandlerTests
{
    private const string Scope = "commerce-api";

    private readonly RecordingCache _tokens = new();
    private string? _sent;

    [Fact]
    public async Task A_401_evicts_the_token_it_sent_and_still_reaches_the_caller()
    {
        using HttpResponseMessage response = await SendAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "evicted and handed back, never retried");
        _tokens.Evicted.ShouldHaveSingleItem().ShouldBe((Scope, _sent));
        _tokens.Issued.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_answer_that_refuses_no_token_evicts_nothing(HttpStatusCode status)
    {
        using HttpResponseMessage response = await SendAsync(new HttpResponseMessage(status));

        _tokens.Evicted.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_grpc_Unauthenticated_on_an_http_200_evicts_the_token_it_sent()
    {
        using HttpResponseMessage response = await SendAsync(TrailersOnly("16"));

        _tokens.Evicted.ShouldHaveSingleItem().ShouldBe((Scope, _sent));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("14")]
    public async Task Another_grpc_status_evicts_nothing(string status)
    {
        // OK, PermissionDenied and Unavailable: none says the token itself was refused.
        using HttpResponseMessage response = await SendAsync(TrailersOnly(status));

        _tokens.Evicted.ShouldBeEmpty();
    }

    /// <summary>A gRPC error sent before any message: its status in the headers of an HTTP 200.</summary>
    private static HttpResponseMessage TrailersOnly(string status)
    {
        HttpResponseMessage answer = new(HttpStatusCode.OK);
        answer.Headers.Add("grpc-status", status);

        return answer;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpResponseMessage answer)
    {
        Answering callee = new(answer);
        ServiceIdentityOptions identity = new()
        {
            ClientId = "web-bff",
            Scope = Scope
        };
        using HttpMessageInvoker invoker = new(
            new ClientCredentialsHandler(_tokens, Options.Create(identity)) { InnerHandler = callee });
        using HttpRequestMessage request = new(HttpMethod.Get, "http://catalog.invalid/");

        HttpResponseMessage response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        _sent = callee.Sent;

        return response;
    }

    /// <summary>The callee, answering one set response and keeping the bearer token it was sent.</summary>
    private sealed class Answering(HttpResponseMessage answer) : HttpMessageHandler
    {
        public string? Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sent = request.Headers.Authorization?.Parameter;

            return Task.FromResult(answer);
        }
    }

    /// <summary>A token source issuing a new token each ask and keeping every eviction.</summary>
    private sealed class RecordingCache : ITokenCache
    {
        private int _issued;

        public int Issued => _issued;

        public List<(string Scope, string? Token)> Evicted { get; } = [];

        public Task<string> GetAsync(string scope, CancellationToken ct) =>
            Task.FromResult($"token-{Interlocked.Increment(ref _issued)}");

        public void Evict(string scope, string token) => Evicted.Add((scope, token));
    }
}
