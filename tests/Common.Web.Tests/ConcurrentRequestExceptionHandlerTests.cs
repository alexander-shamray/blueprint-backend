using System.Net;
using System.Text.Json;
using Common.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§8.5's contention on the wire, as a 409 a client may retry rather than a 500.</summary>
public class ConcurrentRequestExceptionHandlerTests
{
    private static readonly Guid CommandId = Guid.Parse("6f1d2a70-9c3b-4a1e-8f52-1b7c4d905e33");

    [Fact]
    public async Task A_request_already_in_progress_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        // The 500 fallback writes through the same service, so only the status shows this handler answered.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_does_not_echo_the_command_id()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The caller sent it, and it is part of a key another segment of which is the subject (§8.5).
        body.RootElement.GetProperty("detail").GetString()!.ShouldNotContain(CommandId.ToString());
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        using IHost host = await StartThrowingAsync(new ConcurrentRequestException(CommandId));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        // The handler selects: one matching everything would pass the tests above.
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static Task<IHost> StartThrowingAsync(Exception exception) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.Run(_ => throw exception);
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
