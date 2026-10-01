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

/// <summary>ADR-057's refusal on the wire, which unregistered would be a 500 a client retries into again.</summary>
public class CommandIdReusedExceptionHandlerTests
{
    private static readonly Guid CommandId = Guid.Parse("6f1d2a70-9c3b-4a1e-8f52-1b7c4d905e33");

    [Fact]
    public async Task A_reused_command_id_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_names_itself_with_a_code_a_client_can_switch_on()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        body.RootElement.GetProperty("code").GetString().ShouldBe("command.id_reused");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /orders");
        body.RootElement.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_detail_asks_for_a_new_identifier_and_does_not_say_retry()
    {
        using JsonDocument body = await BodyOfAsync(new CommandIdReusedException(CommandId));

        string detail = body.RootElement.GetProperty("detail").GetString()!;

        detail.ShouldContain("already used for a different request");
        detail.ShouldContain("new identifier");
        detail.ShouldNotContain("retry", Case.Insensitive, "a retry under this identifier meets the same refusal");
    }

    [Fact]
    public async Task The_body_does_not_echo_the_command_id()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The whole body, not the detail alone: the id is part of a key whose first segment is the subject (§8.5).
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(CommandId.ToString());
    }

    [Fact]
    public async Task A_client_that_cannot_accept_problem_json_still_gets_the_409()
    {
        using IHost host = await StartThrowingAsync(new CommandIdReusedException(CommandId));
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

    private static async Task<JsonDocument> BodyOfAsync(Exception exception)
    {
        using IHost host = await StartThrowingAsync(exception);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        // The 500 fallback writes through the same service, so only the status shows this handler answered.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
