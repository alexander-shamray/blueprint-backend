using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>The gateway's pipeline (§4.2): probes, §10.4's correlation ID and §10.2's route refusals.</summary>
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

        // Anonymous for the kubelet (§13.5); ready with an empty check set, as the edge owns nothing (§10.1).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        // §10.6's header against the host, since the extension's own suite cannot see whether the gateway calls it.
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

    /// <summary>§10.2's <c>authenticated</c> route policy: the gateway challenges and proxies nothing.</summary>
    [Fact]
    public async Task An_authenticated_route_challenges_a_caller_carrying_no_token()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/orders/018f4c2e", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeProblemJson(response);
    }

    /// <summary>§10.5's one error shape, which a 401 or 403 gets only from <c>UseStatusCodePages</c>.</summary>
    private static async Task ShouldBeProblemJson(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe((int)response.StatusCode);
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>403, not 401, for an authenticated caller without the permission, as §10.5's table draws.</summary>
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
