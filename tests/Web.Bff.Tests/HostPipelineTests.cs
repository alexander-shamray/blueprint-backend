using System.Net;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>That this host wires in §10.6's security headers and §13.5's health endpoints.</summary>
public sealed class HostPipelineTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task Health_probes_answer_without_a_token(string path)
    {
        // Anonymous for the kubelet; ready with the empty set ownsNoReadinessDependencies declares (§13.5).
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
