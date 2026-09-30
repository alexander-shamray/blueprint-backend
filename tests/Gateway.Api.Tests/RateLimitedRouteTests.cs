using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.3's limiter driven until it rejects, which it never does without <c>UseRateLimiter</c>.</summary>
public sealed class RateLimitedRouteTests(StubDestination stub) : IClassFixture<StubDestination>
{
    /// <summary>The <c>anonymous</c> policy's fixed window (§10.3).</summary>
    private const int PermitLimit = 100;

    /// <summary>The <c>authenticated</c> policy's token bucket (§10.3).</summary>
    private const int TokenLimit = 300;

    private const string PublicRoute = "/api/v1/catalog/products";

    /// <summary>Authenticated at the edge, and rate-limited per subject (§10.2).</summary>
    private const string AuthenticatedRoute = "/api/v1/orders/018f4c2e";

    /// <summary>The budget and the §10.5 shape on one exhaustion.</summary>
    [Fact]
    public async Task The_anonymous_window_admits_its_budget_and_refuses_the_next_as_problem_json()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        for (int i = 0; i < PermitLimit; i++)
        {
            HttpResponseMessage permitted = await client.GetAsync(PublicRoute, ct);

            permitted.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {PermitLimit} is inside the window");
        }

        HttpResponseMessage rejected = await client.GetAsync(PublicRoute, ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // Present, not rounded: a minute's window leaves tens of seconds, where floor and ceiling agree.
        rejected.Headers.RetryAfter.ShouldNotBeNull();
        rejected.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        rejected.Headers.RetryAfter.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);

        using JsonDocument body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(ct));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
        body.RootElement.GetProperty("title").GetString().ShouldBe("Too many requests");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <remarks>
    /// It stays green with <c>UseRateLimiter</c> above <c>UseAuthentication</c>, so it does not guard §4.2's order.
    /// </remarks>
    [Fact]
    public async Task The_authenticated_policy_gives_each_subject_its_own_bucket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        // Well under the minute a shared bucket would queue subject-b for, since QueueLimit is not zero.
        client.Timeout = TimeSpan.FromSeconds(15);

        for (int i = 0; i < TokenLimit; i++)
        {
            HttpResponseMessage spent = await Get(client, "subject-a", ct);

            spent.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"request {i + 1} of {TokenLimit} is inside subject-a's bucket");
        }

        HttpResponseMessage other = await Get(client, "subject-b", ct);

        other.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "subject-b holds its own bucket — a shared one would queue this request until replenishment");
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string subject, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, AuthenticatedRoute);
        request.Headers.Add(TestAuthHandler.UserHeader, subject);

        return await client.SendAsync(request, ct);
    }
}
