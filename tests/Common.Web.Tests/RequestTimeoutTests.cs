using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§9.7's service operation total as a deadline a request meets, answered with §10.5's 504 row.</summary>
public partial class RequestTimeoutTests
{
    /// <summary>Short, so the case measures the mechanism rather than waiting out the platform's number.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(300);

    /// <summary>The slack a loaded runner is allowed past the deadline before the answer counts as late.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_stalled_handler_is_answered_504_with_the_timed_out_code_inside_the_deadline()
    {
        using IHost host = await StartAsync();
        using HttpClient client = host.GetTestClient();
        Stopwatch elapsed = Stopwatch.StartNew();

        HttpResponseMessage response = await client.GetAsync("/stalled", TestContext.Current.CancellationToken);

        elapsed.Elapsed.ShouldBeLessThan(Deadline + Slack);
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status504GatewayTimeout);
        body.RootElement.GetProperty("code").GetString().ShouldBe(RequestTimeoutExtensions.TimedOutCode);
        body.RootElement.GetProperty("instance").GetString().ShouldBe("GET /stalled");
        body.RootElement.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_stalled_handler_whose_caller_accepts_no_problem_body_is_still_answered_504()
    {
        using IHost host = await StartAsync();
        using HttpClient client = host.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/stalled");
        request.Headers.Accept.ParseAdd("text/plain");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact]
    public async Task An_endpoint_that_opts_out_outlives_the_deadline()
    {
        using IHost host = await StartAsync();
        using HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/long", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void The_shared_defaults_give_every_request_the_service_operation_total()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        DefaultPolicy(host).Timeout.ShouldBe(ServiceOptions.OperationTimeout);
        DefaultPolicy(host).TimeoutStatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
    }

    [Fact]
    public void A_host_that_names_its_own_deadline_gets_that_one()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();
        TimeSpan longer = ServiceOptions.OperationTimeout * 2;

        builder.AddCommonWebDefaults(longer);

        using IHost host = builder.Build();

        DefaultPolicy(host).Timeout.ShouldBe(longer);
    }

    [Fact]
    public void A_request_at_its_deadline_still_drains_inside_the_hosts_shutdown()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        // §15.3: the drain waits for in-flight requests up to ShutdownTimeout, so a deadline past it is cut short.
        host.Services
            .GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout
            .ShouldBeGreaterThan(DefaultPolicy(host).Timeout!.Value);
    }

    [Fact]
    public void Every_deployable_chart_grants_the_drain_more_than_the_host_takes()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();
        TimeSpan drain = host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;

        string[] charts =
        [
            .. Directory
                .GetDirectories(Path.Combine(RepositoryRoot(), "deploy", "helm"))
                .Where(chart => File.Exists(Path.Combine(chart, "templates", "deployment.yaml")))
        ];

        charts.ShouldNotBeEmpty("no deployable chart was found, so the nesting below would hold of nothing");

        // §15.3: a grace period that only equals the drain is SIGKILL at the instant the host would have finished.
        foreach (string chart in charts)
        {
            Match grace = GracePeriod().Match(File.ReadAllText(Path.Combine(chart, "values.yaml")));

            grace.Success.ShouldBeTrue($"{chart} names no terminationGracePeriodSeconds");
            TimeSpan
                .FromSeconds(int.Parse(grace.Groups["seconds"].Value, CultureInfo.InvariantCulture))
                .ShouldBeGreaterThan(drain, $"{chart}'s grace period");
        }
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"^terminationGracePeriodSeconds:\s*(?<seconds>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex GracePeriod();

    private static RequestTimeoutPolicy DefaultPolicy(IHost host) =>
        host.Services
            .GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy
            .ShouldNotBeNull("no default policy means no request meets a deadline");

    /// <summary>The hosts' order: the exception handler above, which would answer a timed-out request 499.</summary>
    private static Task<IHost> StartAsync() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();

                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddCommonProblemDetails();
                    services.AddCommonRequestTimeouts(Deadline);
                });

                web.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseCorrelationId();
                    app.UseRouting();
                    app.UseRequestTimeouts();

                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet(
                            "/stalled",
                            async (CancellationToken ct) =>
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                                return Results.Ok();
                            });

                        endpoints
                            .MapGet(
                                "/long",
                                async (CancellationToken ct) =>
                                {
                                    await Task.Delay(Deadline * 3, ct);
                                    return Results.Ok();
                                })
                            .DisableRequestTimeout();
                    });
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync();
}
