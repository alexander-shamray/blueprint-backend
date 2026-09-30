using System.Net;
using Ordering.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The host builds under <c>ValidateOnBuild</c> and serves its probes (§13.5) and OpenAPI document.</summary>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    // .invalid never resolves, so both checks fail on NXDOMAIN; Connect Timeout=1 bounds a resolver that answers.
    // Declared once, so the two factories below cannot disagree.
    private const string UnreachableSql =
        "Server=ordering-sql.invalid,1433;Database=Ordering;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    private const string UnreachableRabbit = "amqp://guest:guest@ordering-rabbit.invalid:5672";

    /// <summary>The same host with the base factory's <c>TestAuthHandler</c>, so a caller can authenticate.</summary>
    public sealed class AuthenticatedUnreachableFactory()
        : OrderingApiFactory(UnreachableSql, UnreachableRabbit);

    public sealed class UnreachableInfrastructureFactory()
        : OrderingApiFactory(UnreachableSql, UnreachableRabbit)
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
        // Also that readiness has not leaked into liveness (§13.5): the checks this host registers are unreachable.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_redis_and_bus_checks()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // The count is the assertion, so a dropped readiness check goes red.
        options.Registrations.Count.ShouldBe(4);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        // Both Redis servers, since §8.1 gives them different eviction policies and AbortOnConnectFail is false.
        HealthCheckRegistration cache = options.Registrations.Single(r => r.Name == "redis-cache");
        cache.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        HealthCheckRegistration coordination =
            options.Registrations.Single(r => r.Name == "redis-coordination");
        coordination.Tags.ShouldContain("ready", "§8.5's claims are written to this instance");

        // Registered by AddMassTransit itself, so a MassTransit release that renames it fails here.
        HealthCheckRegistration bus = options.Registrations.Single(r => r.Name == "masstransit-bus");
        bus.Tags.ShouldContain("ready", "a bus check outside the ready predicate reports to nobody");
        bus.Tags.ShouldContain("masstransit", "both tags are the documented contract (§13.5), so both are pinned");
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
    public async Task OpenApi_document_is_not_anonymous()
    {
        // MapOpenApi carries no metadata, so the fallback policy covers it (ADR-030).
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OpenApi_document_is_served_to_a_caller()
    {
        // The half a 401 cannot show: the test above would pass if the document stopped generating (ADR-030).
        using AuthenticatedUnreachableFactory authenticated = new();
        using HttpClient client = authenticated.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response =
            await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }
}
