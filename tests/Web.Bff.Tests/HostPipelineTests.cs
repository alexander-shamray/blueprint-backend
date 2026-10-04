using System.Net;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>That this host wires in §10.6's security headers and §13.5's health endpoints.</summary>
public sealed class HostPipelineTests
{
    [Fact]
    public async Task Liveness_answers_without_a_token()
    {
        // Anonymous for the kubelet, and gated on nothing, so it holds with the database unreachable (§13.5).
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task Readiness_answers_without_a_token_and_reports_the_database_it_cannot_reach(string path)
    {
        // 503 rather than 401: anonymous, and gated on a SQL Server this factory cannot reach (§13.5).
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }
}
