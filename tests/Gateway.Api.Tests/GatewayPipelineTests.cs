using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// The gateway's own pipeline (§4.2), driven through the real host: the
/// probes, the correlation ID of §10.4, and the two refusals §10.2's route
/// policies exist to produce.
/// </summary>
/// <remarks>
/// Every refusal here is answered above the proxy — authentication and
/// authorization both run before <c>MapReverseProxy</c>'s endpoint — so no
/// destination is dialled and no name is resolved. The tests whose request has
/// to reach the proxy live in <see cref="ProxiedRouteTests"/>, over a stub
/// server on loopback.
/// </remarks>
public sealed class GatewayPipelineTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task Health_probes_answer_without_a_token(string path)
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Ready and startup are healthy with an empty check set, which is
        // correct for a host whose dependencies do not gate readiness (§13.5):
        // the gateway proxies four services and deliberately declines to report
        // unready when one of them is down, which would take the edge out of
        // rotation for a fault it is meant to pass through. And the reason the
        // probes must stay anonymous is that the kubelet carries no token, so
        // the gateway would otherwise be the one component its own auth
        // pipeline could kill.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        // §10.6's header, asserted against the host rather than against the
        // extension. `SecurityHeadersTests` proves what UseSecurityHeaders
        // does; only this says the gateway calls it — delete the line from
        // Program.cs and every test in Common.Web.Tests stays green, which is
        // the failure that commit named and then left uncovered on two of the
        // four hosts.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task A_request_arriving_without_a_correlation_id_is_given_one()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? values).ShouldBeTrue();
        values!.Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_correlation_id_the_client_supplied_is_the_one_that_comes_back()
    {
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "018f4c2e-supplied");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("018f4c2e-supplied");
    }

    /// <summary>
    /// The <c>authenticated</c> authorization policy of §10.2, on the route
    /// rather than at the service. Nothing is proxied: the challenge is
    /// answered by the gateway, which is the whole point of putting the policy
    /// on the route.
    /// </summary>
    [Fact]
    public async Task An_authenticated_route_challenges_a_caller_carrying_no_token()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/orders/018f4c2e", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeProblemJson(response);
    }

    /// <summary>
    /// §10.5's opening promise, applied to the two statuses §10.5 itself says
    /// no handler produces: "every service returns RFC 9457
    /// <c>application/problem+json</c>, so clients handle one error shape
    /// regardless of which service produced it".
    /// </summary>
    /// <remarks>
    /// <c>AddProblemDetails</c> registers a writer and nothing calls it for an
    /// authentication challenge or an authorization forbid — those are written
    /// by the middleware before any endpoint runs, and they carry no body at
    /// all. So the one error shape had two holes in it, on the two statuses a
    /// browser client meets first. Raised by Copilot against the route
    /// policies this PR introduced; it was true of every service host since
    /// PR-16, which is why the fix is in <c>Common.Web</c>.
    /// </remarks>
    private static async Task ShouldBeProblemJson(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe((int)response.StatusCode);
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The gateway's own permission policy, refusing a caller who
    /// authenticated and does not hold <c>inventory:admin</c>. 403 rather than
    /// 401, which is the distinction §10.5's table draws.
    /// </summary>
    [Fact]
    public async Task The_admin_route_refuses_an_authenticated_caller_without_the_permission()
    {
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/stock");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldBeProblemJson(response);
    }

    /// <summary>
    /// The second permission policy the gateway registers, asserted separately
    /// from the first: the two resolve through the same provider, and a route
    /// that named a policy nobody registered would refuse every caller rather
    /// than only this one.
    /// </summary>
    [Fact]
    public async Task The_payments_admin_route_refuses_an_authenticated_caller_without_the_permission()
    {
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/payments/{Guid.CreateVersion7()}");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldBeProblemJson(response);
    }
}
