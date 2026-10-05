using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>The probe against a real loopback listener serving §13.5's endpoints.</summary>
public class HealthProbeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private sealed class Counted(HealthStatus status, TimeSpan delay) : IHealthCheck
    {
        public int Calls;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(delay, ct);
            return new HealthCheckResult(status);
        }
    }

    private sealed class SlowAfterFirst(TimeSpan delay) : IHealthCheck
    {
        public readonly TaskCompletionSource Slowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                Slowed.TrySetResult();
                await Task.Delay(delay, ct);
            }

            return HealthCheckResult.Healthy();
        }
    }

    private static async Task<(WebApplication App, string Port)> StartAsync(IHealthCheck check)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHealthChecks().AddCheck("sql", check, tags: ["ready"]);

        WebApplication app = builder.Build();
        app.MapCommonHealthEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);

        return (app, new Uri(app.Urls.First()).Port.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_ready_host_exits_zero_having_asked_its_readiness_endpoint()
    {
        Counted check = new(HealthStatus.Healthy, TimeSpan.Zero);
        (WebApplication app, string port) = await StartAsync(check);
        await using (app)
        {
            int exit = await HealthProbe.RunAsync(port, Generous);

            exit.ShouldBe(0);
            check.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task An_unready_host_exits_one()
    {
        Counted check = new(HealthStatus.Unhealthy, TimeSpan.Zero);
        (WebApplication app, string port) = await StartAsync(check);
        await using (app)
        {
            int exit = await HealthProbe.RunAsync(port, Generous);

            exit.ShouldBe(1);
            check.Calls.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_refused_connection_exits_one()
    {
        // A port bound and released, so nothing listens on it.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        int exit = await HealthProbe.RunAsync(port.ToString(CultureInfo.InvariantCulture), Generous);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task A_readiness_answer_slower_than_the_timeout_exits_one()
    {
        // Healthy once it answers, so only the timeout can make this a 1; the warm-up spends the cold
        // pipeline's start outside the timed probe, whose budget then reaches the slow check.
        SlowAfterFirst check = new(Generous);
        (WebApplication app, string port) = await StartAsync(check);
        await using (app)
        {
            (await HealthProbe.RunAsync(port, Generous)).ShouldBe(0);

            int exit = await HealthProbe.RunAsync(port, TimeSpan.FromSeconds(1));

            exit.ShouldBe(1);
            await check.Slowed.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task No_configured_port_exits_one()
    {
        int exit = await HealthProbe.RunAsync(null, Generous);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task The_first_of_several_ports_is_the_one_asked()
    {
        Counted check = new(HealthStatus.Healthy, TimeSpan.Zero);
        (WebApplication app, string port) = await StartAsync(check);
        await using (app)
        {
            int exit = await HealthProbe.RunAsync($"{port};1", Generous);

            exit.ShouldBe(0);
            check.Calls.ShouldBe(1);
        }
    }
}
