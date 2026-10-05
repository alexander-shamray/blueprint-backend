using System.Net;
using Payments.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>The host builds under <c>ValidateOnBuild</c> and serves its probes (§13.5) and OpenAPI document.</summary>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    // .invalid never resolves, so both checks fail on NXDOMAIN; Connect Timeout=1 bounds a resolver that answers.
    // Declared once, so the two factories below cannot disagree.
    internal const string UnreachableSql =
        "Server=payments-sql.invalid,1433;Database=Payments;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    internal const string UnreachableRabbit = "amqp://guest:guest@payments-rabbit.invalid:5672";

    /// <summary>The same host with the base factory's <c>TestAuthHandler</c>, so a caller can authenticate.</summary>
    public sealed class AuthenticatedUnreachableFactory()
        : PaymentsApiFactory(UnreachableSql, UnreachableRabbit);

    public sealed class UnreachableInfrastructureFactory()
        : PaymentsApiFactory(UnreachableSql, UnreachableRabbit)
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
        // Also that readiness has not leaked into liveness (§13.5): both checks this host registers are unreachable.
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

    [Fact]
    public async Task OpenApi_document_is_not_anonymous()
    {
        // MapOpenApi has no authorization metadata, so AddCommonWebDefaults' fallback policy reaches it (§11.4).
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OpenApi_document_is_served_to_a_caller()
    {
        // The half a 401 cannot show: that the document still generates.
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
