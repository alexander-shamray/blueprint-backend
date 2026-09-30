using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class HealthEndpointTests
{
    private sealed class Always(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct) =>
            Task.FromResult(new HealthCheckResult(status));
    }

    // Defaults to false, as in production, so a test registering no readiness check has to say so.
    private static Task<IHost> StartAsync(
        Action<IHealthChecksBuilder> checks,
        bool ownsNoReadinessDependencies = false) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    checks(services.AddHealthChecks());
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapCommonHealthEndpoints(ownsNoReadinessDependencies));
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .StartAsync(TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> GetAsync(IHost host, string path) =>
        host.GetTestClient().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Liveness_passes_with_no_checks_registered()
    {
        using IHost host = await StartAsync(_ => { }, ownsNoReadinessDependencies: true);

        HttpResponseMessage response = await GetAsync(host, "/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_ignores_a_failing_dependency_that_readiness_reports()
    {
        // §13.5: a liveness check on the database would restart every pod at once.
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Unhealthy), tags: ["ready"]));

        HttpResponseMessage live = await GetAsync(host, "/health/live");
        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Readiness_passes_when_its_checks_pass()
    {
        using IHost host = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Healthy), tags: ["ready"]));

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_ignores_an_observe_tagged_check()
    {
        // The outbox is alerted on (§13.6) and never probed, as a backlog is not an outage.
        using IHost host = await StartAsync(
            checks => checks.AddCheck("outbox", new Always(HealthStatus.Unhealthy), tags: ["observe"]),
            ownsNoReadinessDependencies: true);

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Startup_gates_on_the_ready_tagged_checks()
    {
        // Asserted by request: the kubelet's startupProbe holds its own copy of the route,
        // in a manifest no compiler reads.
        using (IHost healthy = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Healthy), tags: ["ready"])))
        {
            HttpResponseMessage startup = await GetAsync(healthy, "/health/startup");

            startup.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The control, since an empty predicate set also answers 200.
        using IHost failing = await StartAsync(checks =>
            checks.AddCheck("sql", new Always(HealthStatus.Unhealthy), tags: ["ready"]));

        HttpResponseMessage unavailable = await GetAsync(failing, "/health/startup");

        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Every_probe_allows_anonymous()
    {
        // On the metadata, since this host has no policy for an anonymous request to fail (§13.5).
        using IHost host = await StartAsync(_ => { }, ownsNoReadinessDependencies: true);

        IReadOnlyList<Endpoint> endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints;

        endpoints.Count.ShouldBe(3);
        foreach (Endpoint endpoint in endpoints)
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull(endpoint.DisplayName);
    }

    [Fact]
    public async Task A_host_with_no_readiness_check_refuses_to_start()
    {
        // §13.5's fail-open: an empty predicate set passes, and §15.1 relies on this probe gating a rollout.
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => StartAsync(_ => { }));

        thrown.Message.ShouldContain("ready");
    }

    [Fact]
    public async Task An_observe_tagged_check_does_not_satisfy_the_guard()
    {
        // An observe-tagged check alone selects nothing, so it is the empty case.
        await Should.ThrowAsync<InvalidOperationException>(
            () => StartAsync(checks =>
                checks.AddCheck("outbox", new Always(HealthStatus.Healthy), tags: ["observe"])));
    }

    [Fact]
    public async Task A_host_that_declares_an_empty_readiness_set_starts_and_reports_ready()
    {
        // The gateway and the BFF, which declare that no hop of theirs gates readiness.
        using IHost host = await StartAsync(_ => { }, ownsNoReadinessDependencies: true);

        HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
