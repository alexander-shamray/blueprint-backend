using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§10.5's 409 row on the wire, in §4.2's pipeline shape with the exception handler outermost.</summary>
public class ConcurrencyExceptionHandlerTests
{
    [Fact]
    public async Task A_concurrency_exception_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        // The 500 fallback carries the same fields, so only the status shows this handler answered.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // §10.5's one error shape, which a status written around IProblemDetailsService loses.
        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders/cancel");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_names_no_entity_and_no_row_version()
    {
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("Order 42 had RowVersion 0x0B"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The framework's message names storage details (§7.3).
        string detail = body.RootElement.GetProperty("detail").GetString()!;
        detail.ShouldNotContain("RowVersion");
        detail.ShouldNotContain("42");
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        // Echoing TryWriteAsync's false would turn a retryable race into a 500.
        using IHost host = await StartThrowingAsync(new DbUpdateConcurrencyException("stale"));
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_plain_update_exception_is_not_a_conflict()
    {
        // The base DbUpdateException also covers a violated constraint, which a retry cannot fix.
        using IHost host = await StartThrowingAsync(new DbUpdateException("constraint"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Any_other_exception_still_falls_through_to_the_500()
    {
        using IHost host = await StartThrowingAsync(new InvalidOperationException("boom"));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders/cancel", TestContext.Current.CancellationToken);

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
