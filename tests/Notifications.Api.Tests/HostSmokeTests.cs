using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Notifications.Api.Tests;

/// <summary>The host builds under <c>ValidateOnBuild</c>, serves its probes and runs none of the worker's.</summary>
public class HostSmokeTests(NotificationsApiFactory factory) : IClassFixture<NotificationsApiFactory>
{
    [Fact]
    public async Task Live_probe_returns_200()
    {
        // Also that readiness has not leaked into liveness (§13.5): the one check this host registers is unreachable.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_check_alone()
    {
        // By name, since a bus check here would say the host had composed the worker's half (ADR-118).
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        options.Registrations.Select(r => r.Name).ShouldBe(["sql"]);
        options.Registrations.Single().Tags.ShouldContain("ready", "an untagged check is invisible to /health/ready");
    }

    [Fact]
    public async Task Ready_probe_returns_503_when_the_database_is_unreachable()
    {
        // The other half: this fails if the ready predicate stops selecting the registered check.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void The_host_runs_none_of_the_workers_background_work()
    {
        // A second process running the consumers or the send pass would send every notice twice (ADR-118).
        string[] own =
        [
            .. factory.Services
                .GetServices<IHostedService>()
                .Select(service => service.GetType())
                .Where(type => type.Assembly.GetName().Name is { } name &&
                    (name.StartsWith("Notifications.", StringComparison.Ordinal) ||
                        name.StartsWith("MassTransit", StringComparison.Ordinal)))
                .Select(type => type.FullName ?? type.Name)
        ];

        own.ShouldBeEmpty($"the API composed the worker's half: {string.Join(", ", own)}");
    }

    [Fact]
    public void The_grpc_endpoint_speaks_http2_alone_and_the_probes_http1()
    {
        // TestServer ignores Kestrel's endpoints, so the shipped settings are what is asserted (§9.7).
        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();

        configuration["Kestrel:Endpoints:Grpc:Url"].ShouldBe("http://0.0.0.0:8081");
        configuration["Kestrel:Endpoints:Grpc:Protocols"].ShouldBe("Http2");
        configuration["Kestrel:Endpoints:Rest:Url"].ShouldBe("http://0.0.0.0:8080");
        configuration["Kestrel:Endpoints:Rest:Protocols"].ShouldBe("Http1");
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        // The building block owns the header (§10.6); this asserts the host composes it, at the cheapest response.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task An_unknown_path_is_challenged_rather_than_missing()
    {
        // A fallback policy is evaluated even when routing matched nothing, so an unknown path is a 401 (ADR-030).
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/v1/no-such-path", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
