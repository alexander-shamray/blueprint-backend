using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.1's body ceiling over Kestrel, as <c>TestServer</c> has no body-size feature (§12.4).</summary>
/// <remarks>Every case carries a token: the ceiling is read in the forwarder, below authentication (§10.1).</remarks>
public sealed class RequestSizeLimitTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>A route that accepts a body from an ordinary authenticated principal (§10.2).</summary>
    private const string Route = "/api/v1/orders";

    /// <summary>Exactly the ceiling passes, which separates a configured limit from a limit of zero.</summary>
    [Fact]
    public async Task A_body_at_the_ceiling_is_forwarded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Post(GatewayLimits.MaxRequestBodyBytes, stub.Address, ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "the stub answers 204, so this reached the destination");
    }

    /// <summary>§10.5's 413 row: the forwarder writes no body, so the status-code pages write the problem.</summary>
    [Fact]
    public async Task A_body_past_the_ceiling_is_refused_as_problem_json()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage response = await Post(GatewayLimits.MaxRequestBodyBytes + 1, stub.Address, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(413);
        body.RootElement.GetProperty("instance").GetString().ShouldBe($"POST {Route}");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("code", out _)
            .ShouldBeFalse("the forwarder answered the exception, so no exception handler wrote this problem");
    }

    /// <summary>A chunked body is counted as it arrives, the same limit's second enforcement point.</summary>
    [Fact]
    public async Task A_chunked_body_past_the_ceiling_is_refused_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using GatewayOnKestrel gateway = new(stub.Address);

        using HttpRequestMessage request = Authenticated(HttpMethod.Post, Route);
        request.Content = new StreamContent(new UnknownLengthStream(GatewayLimits.MaxRequestBodyBytes + 1));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;

        request.Content.Headers.ContentLength.ShouldBeNull("otherwise this is the previous test again");

        HttpResponseMessage response = await gateway.Client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    /// <summary>Posts with <c>Expect: 100-continue</c>, without which an abortive close can discard the 413.</summary>
    private static async Task<HttpResponseMessage> Post(long bytes, string destination, CancellationToken ct)
    {
        using GatewayOnKestrel gateway = new(destination);

        using HttpRequestMessage request = Authenticated(HttpMethod.Post, Route);
        request.Content = new ByteArrayContent(new byte[bytes]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.ExpectContinue = true;

        return await gateway.Client.SendAsync(request, ct);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string route)
    {
        HttpRequestMessage request = new(method, route);
        request.Headers.Add(TestAuthHandler.UserHeader, "subject-a");

        return request;
    }

    /// <summary>A length nobody can ask, since <c>StreamContent</c> sends one for a seekable stream.</summary>
    private sealed class UnknownLengthStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int served = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, served);
            _remaining -= served;

            return served;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The stubbed gateway on Kestrel, set before <c>CreateClient</c> initialises it (§12.4).</summary>
    private sealed class GatewayOnKestrel : IDisposable
    {
        private readonly StubbedGatewayFactory _factory;

        public GatewayOnKestrel(string destination)
        {
            _factory = new StubbedGatewayFactory(destination);
            _factory.UseKestrel(0);
            _factory.StartServer();

            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }
}
