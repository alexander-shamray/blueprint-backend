using System.Net;
using System.Text.Json;
using Common.Web;
using Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>The edge's own deadline, the outermost of §9.7's layers, read off the built host.</summary>
public sealed class RequestTimeoutTests(StubDestination stub) : IClassFixture<StubDestination>
{
    [Fact]
    public void The_edge_waits_longer_than_a_service_and_less_than_its_own_drain()
    {
        using GatewayFactory factory = new();
        using HttpClient client = factory.CreateClient();

        TimeSpan deadline = factory.Services
            .GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy!.Timeout!.Value;

        deadline.ShouldBe(GatewayLimits.RequestTimeout);

        // Decreasing inwards (§9.7): a service's 504 reaches the client before the edge gives up on it.
        deadline.ShouldBeGreaterThan(ServiceOptions.OperationTimeout);

        // §15.3: a request in flight at SIGTERM has to finish inside the drain.
        deadline.ShouldBeLessThan(
            factory.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task A_destination_that_never_answers_is_answered_504_with_the_timed_out_code()
    {
        using ImpatientGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/catalog/products?{StubDestination.StallQuery}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("code").GetString().ShouldBe(RequestTimeoutExtensions.TimedOutCode);
    }

    /// <summary>The edge with a deadline short enough to wait out, keeping the shared writer.</summary>
    private sealed class ImpatientGatewayFactory(string destination) : StubbedGatewayFactory(destination)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
                services.AddCommonRequestTimeouts(TimeSpan.FromMilliseconds(300)));
        }
    }
}
