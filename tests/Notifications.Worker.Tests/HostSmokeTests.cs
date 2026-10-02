using System.Net;
using Notifications.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The host builds under <c>ValidateOnBuild</c> and serves its probes (§13.5).</summary>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    // .invalid never resolves, so the checks fail on NXDOMAIN; Connect Timeout=1 bounds a resolver that answers.
    // Declared once, so the two factories below cannot disagree.
    private const string UnreachableSql =
        "Server=notifications-sql.invalid,1433;Database=Notifications;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    private const string UnreachableRabbit = "amqp://guest:guest@notifications-rabbit.invalid:5672";

    /// <summary>The same host with the base factory's <c>TestAuthHandler</c>, so a caller can authenticate.</summary>
    public sealed class AuthenticatedUnreachableFactory()
        : NotificationsWorkerFactory(UnreachableSql, UnreachableRabbit);

    public sealed class UnreachableInfrastructureFactory()
        : NotificationsWorkerFactory(UnreachableSql, UnreachableRabbit)
    {
        /// <summary>The one host keeping the production JWT scheme: a test scheme cannot prove its absence.</summary>
        protected override void ConfigureAuthentication(IServiceCollection services)
        {
            // Deliberately empty: restoring the base call would make this host a fixture rather than a deployment.
        }
    }

    [Fact]
    public async Task Live_probe_returns_200()
    {
        // Also that readiness has not leaked into liveness (§13.5): every check this host registers is unreachable.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_and_bus_checks()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // The count is the assertion, since a readiness check dropped from a growing list turns nothing red.
        options.Registrations.Count.ShouldBe(2);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        // Registered by AddMassTransit itself, and pinned so a MassTransit major that changes it fails here.
        HealthCheckRegistration bus = options.Registrations.Single(r => r.Name == "masstransit-bus");
        bus.Tags.ShouldContain("ready", "a bus check outside the ready predicate reports to nobody");
        bus.Tags.ShouldContain("masstransit", "both tags are the documented contract (§13.5), so both are pinned");

        // By name, since a dependency added to readiness later is a rollout the relay could block (§15.3).
        options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);
    }

    [Fact]
    public async Task Ready_probe_returns_503_when_dependencies_are_unreachable()
    {
        // The other half: this fails if the ready predicate stops selecting the registered checks.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
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

    [Fact]
    public async Task An_unknown_path_is_a_404_to_a_caller()
    {
        // With a caller the same request is a 404, so the 401 above is about authorization, not routing.
        using AuthenticatedUnreachableFactory authenticated = new();
        using HttpClient client = authenticated.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/no-such-path");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response =
            await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
