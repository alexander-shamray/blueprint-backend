using System.Diagnostics;
using System.Net;
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

public class CorrelationIdMiddlewareTests
{
    private const string Header = "X-Correlation-Id";

    [Fact]
    public async Task A_request_without_the_header_is_assigned_an_id()
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        CorrelationIdOf(response).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_supplied_id_is_kept()
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(Header, "from-the-gateway");

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        CorrelationIdOf(response).ShouldBe("from-the-gateway");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_id_is_replaced_rather_than_propagated(string supplied)
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(Header, supplied);

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // FirstOrDefault on an absent header is null, but an empty value is
        // not — and would otherwise become a correlation ID of "" (§10.4).
        CorrelationIdOf(response).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_id_is_readable_by_the_middleware_below()
    {
        // UseExceptionHandler sits above this middleware, so §10.5's body reads the ID from Request.Headers.
        using IHost host = await TestPipeline.StartAsync(
            async context => await context.Response.WriteAsync(context.Request.Headers[Header]!));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        string seenBelow = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        seenBelow.ShouldBe(CorrelationIdOf(response));
    }

    [Fact]
    public async Task The_current_trace_supplies_the_id_when_the_client_does_not()
    {
        using Activity activity = new Activity("incoming").Start();
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // One request, one ID, in both the log pipeline and the trace backend.
        CorrelationIdOf(response).ShouldBe(activity.TraceId.ToString());
    }

    [Fact]
    public async Task A_request_with_no_trace_is_assigned_a_new_identifier()
    {
        Activity.Current = null;
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // "D" format: a TraceId parses as a GUID under "N", so only this tells §10.4's fallbacks apart.
        Guid.TryParseExact(CorrelationIdOf(response), "D", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_id_is_pushed_onto_the_log_scope()
    {
        RecordingLoggerProvider logs = new();
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask, logs);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        IReadOnlyDictionary<string, object> scope = logs.Scopes
            .OfType<IReadOnlyDictionary<string, object>>()
            .ShouldHaveSingleItem();
        scope["CorrelationId"].ShouldBe(CorrelationIdOf(response));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    [InlineData("angle<bracket>")]
    [InlineData("quote\"mark")]
    [InlineData("percent%2f")]
    public async Task An_implausible_id_is_replaced_rather_than_echoed(string supplied)
    {
        // Unauthenticated input (§4.2) echoed in header, body and log scope, so refused, not sanitised.
        string? echoed = await EchoedFor(supplied);

        echoed.ShouldNotBe(supplied);
        echoed.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_over_long_id_is_replaced()
    {
        // Every record the request produces inherits the value, so its length is the platform's cost.
        string supplied = new('a', CorrelationIdExtensions.MaxSuppliedLength + 1);

        (await EchoedFor(supplied)).ShouldNotBe(supplied);
    }

    [Fact]
    public async Task An_id_at_the_bound_is_kept()
    {
        // The control: a middleware replacing everything would pass the test above.
        string supplied = new('a', CorrelationIdExtensions.MaxSuppliedLength);

        (await EchoedFor(supplied)).ShouldBe(supplied);
    }

    [Theory]
    [InlineData("018f4c2e-0000-7000-8000-000000000000")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("from_the_gateway")]
    public async Task A_plausible_id_is_still_adopted(string supplied)
    {
        // §10.4 promises a caller's own ID survives the hop, so the alphabet admits what others mint.
        (await EchoedFor(supplied)).ShouldBe(supplied);
    }

    [Fact]
    public async Task The_id_is_on_the_response_that_UseExceptionHandler_writes()
    {
        // UseExceptionHandler clears the response before §10.5's body, as ADR-031 records for nosniff.
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddCommonProblemDetails());
                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseCorrelationId();
                    app.Run(_ => throw new InvalidOperationException("boom"));
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(Header, "from-the-gateway");

        HttpResponseMessage response =
            await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        CorrelationIdOf(response).ShouldBe("from-the-gateway");
    }

    // Drives the real pipeline with one supplied header and returns what the response echoed.
    private static async Task<string?> EchoedFor(string supplied)
    {
        using IHost host = await TestPipeline.StartAsync(_ => Task.CompletedTask);
        using HttpClient client = host.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(Header, supplied);

        HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        return CorrelationIdOf(response);
    }

    private static string? CorrelationIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(Header, out IEnumerable<string>? values) ? values.Single() : null;
}
