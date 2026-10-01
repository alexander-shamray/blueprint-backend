using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

/// <summary>§8.5's durable refusal on the wire, which unregistered would be a retryable 500.</summary>
public class CommandAlreadyCommittedExceptionHandlerTests
{
    private const string Key =
        "0195e4b2-0000-7000-8000-00000000000a:ordering.order.place:0195e4b2-0000-7000-8000-0000000000ff";

    [Fact]
    public async Task A_command_that_already_committed_becomes_a_409()
    {
        using IHost host = await StartThrowingAsync(new CommandAlreadyCommittedException(Key));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_409_carries_the_same_customisation_as_every_other_problem_response()
    {
        using IHost host = await StartThrowingAsync(new CommandAlreadyCommittedException(Key));
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
    public async Task The_detail_does_not_echo_the_key()
    {
        using IHost host = await StartThrowingAsync(new CommandAlreadyCommittedException(Key));
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The key's first segment is a principal's identity (§8.5), so none of it is echoed.
        body.RootElement.GetProperty("detail").GetString()!.ShouldNotContain(Key);
    }

    [Fact]
    public async Task The_two_409s_are_told_apart_by_what_they_tell_the_caller_to_do()
    {
        // Two of §10.5's 409s say opposite things: retry, and already applied.
        string committed = await DetailOfAsync(new CommandAlreadyCommittedException(Key));
        string inProgress = await DetailOfAsync(new ConcurrentRequestException(Guid.CreateVersion7()));

        committed.ShouldContain("already been applied");
        committed.ShouldContain("read the resource");
        committed.ShouldNotBe(inProgress);
    }

    [Fact]
    public async Task The_409s_carry_distinct_machine_readable_codes()
    {
        // A client switches on §10.5's `code`, not on prose, so the codes are pinned.
        string committed = await CodeOfAsync(new CommandAlreadyCommittedException(Key));
        string inProgress = await CodeOfAsync(new ConcurrentRequestException(Guid.CreateVersion7()));
        string conflict = await CodeOfAsync(new DbUpdateConcurrencyException("stale"));
        string reused = await CodeOfAsync(new CommandIdReusedException(Guid.CreateVersion7()));

        committed.ShouldBe("command.already_committed");
        inProgress.ShouldBe("request.in_progress");
        conflict.ShouldBe("request.concurrency_conflict");
        reused.ShouldBe("command.id_reused");

        // Distinct as a set, since a code shared by two producers would satisfy each assertion alone.
        string[] codes = [committed, inProgress, conflict, reused];
        codes.Distinct().Count().ShouldBe(codes.Length);
    }

    private static async Task<string> CodeOfAsync(Exception exception)
    {
        using IHost host = await StartThrowingAsync(exception);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return body.RootElement.GetProperty("code").GetString()!;
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

    private static async Task<string> DetailOfAsync(Exception exception)
    {
        using IHost host = await StartThrowingAsync(exception);
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/orders", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return body.RootElement.GetProperty("detail").GetString()!;
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
