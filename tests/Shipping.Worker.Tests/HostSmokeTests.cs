using System.Net;
using Shipping.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The host builds under <c>ValidateOnBuild</c> and answers what an empty
/// worker can already be asked: the probes (§13.5), which §15.3 makes its one
/// listener. One factory for the class, since nothing mutates the host.
/// </summary>
/// <remarks>§13.5's rule is that a host with a connection string has a
/// readiness check and one without does not, and both registrations throw on
/// a missing key — so supplying the strings is not a workaround. They name
/// hosts that cannot resolve on purpose, because these tests are about the
/// wiring and not about the engines behind it.</remarks>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    /// <summary>
    /// A parameterless factory, because that is what <c>IClassFixture</c> can
    /// construct. <c>.invalid</c> is reserved and never resolves, so both
    /// checks fail on NXDOMAIN rather than on a timeout, and
    /// <c>Connect Timeout=1</c> bounds the case where a resolver answers
    /// anyway. The bus needs no such bound: <c>WaitUntilStarted</c> is false.
    /// </summary>
    // The two literals both factories below take. Declared once because they
    // are the same host under two schemes, and a pair that drifted would
    // leave the factories describing different deployments.
    private const string UnreachableSql =
        "Server=shipping-sql.invalid,1433;Database=Shipping;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    private const string UnreachableRabbit = "amqp://guest:guest@shipping-rabbit.invalid:5672";

    /// <summary>
    /// The same unreachable host with the <c>TestAuthHandler</c> scheme the
    /// base factory installs, so a caller can authenticate: the fallback
    /// policy (§11.4) lets the production-scheme factory prove only that a
    /// caller is challenged, and whether an unknown path is still a 404
    /// needs one who gets through — the cheapest, since it reaches nothing.
    /// </summary>
    public sealed class AuthenticatedUnreachableFactory()
        : ShippingWorkerFactory(UnreachableSql, UnreachableRabbit);

    public sealed class UnreachableInfrastructureFactory()
        : ShippingWorkerFactory(UnreachableSql, UnreachableRabbit)
    {
        /// <summary>
        /// This service's one host that keeps the production JWT scheme. Every
        /// other factory swaps in <c>TestAuthHandler</c>, which is what lets
        /// those suites authenticate at all — and precisely why none of them
        /// can say whether its headers mean anything to a real deployment. A
        /// test scheme cannot prove its own absence.
        /// </summary>
        protected override void ConfigureAuthentication(IServiceCollection services)
        {
            // Deliberately empty. Not "not yet" — this host is the only one
            // that reads as a deployment rather than a fixture, and restoring
            // the base call would silently take that with it. The forged-header
            // suite that reads it arrives with the first endpoint to forge
            // against.
        }
    }

    [Fact]
    public async Task Live_probe_returns_200()
    {
        // Also the assertion that readiness has not leaked into liveness.
        // §13.5 forbids liveness touching a dependency — "a brief database
        // outage restarts every pod simultaneously" — and both checks this
        // host registers are unreachable, so a liveness probe that consulted
        // either would answer 503 here.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Ready_probe_reports_the_sql_and_bus_checks()
    {
        // Registration, read without a network round trip. §13.5's concern is
        // that "reports ready immediately" and "readiness was never wired up"
        // are indistinguishable from outside, so the wiring is asserted
        // directly rather than inferred from a status code.
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Two, and the count is the assertion rather than a detail of it: an
        // inventory that only ever grows silently is how a readiness check
        // gets dropped without anything going red.
        options.Registrations.Count.ShouldBe(2);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        // Registered by AddMassTransit itself, not by AddShippingInfrastructure
        // — name and tags read from the 8.5.3 source, asserted here so a
        // MassTransit major that changes either fails this test rather than a
        // cluster's readiness.
        HealthCheckRegistration bus = options.Registrations.Single(r => r.Name == "masstransit-bus");
        bus.Tags.ShouldContain("ready", "a bus check outside the ready predicate reports to nobody");
        bus.Tags.ShouldContain("masstransit", "both tags are the documented contract (§13.5), so both are pinned");
    }

    [Fact]
    public async Task Ready_probe_returns_503_when_dependencies_are_unreachable()
    {
        // The other half of the pair above. The registration test fails if the
        // AddSqlServer line is deleted; this one fails if the checks are
        // registered but the predicate stops selecting them. Neither alone
        // catches both.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Every_response_carries_nosniff()
    {
        // The building block owns the header (§10.6); this asserts the host
        // actually calls it. A middleware registered in Common.Web and composed
        // by nobody sets no header on anything, and every test in
        // Common.Web.Tests would still pass.
        //
        // Driven at the liveness probe because it needs no caller and no
        // dependency — the header is on every response, so the cheapest one
        // answers the question.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task An_unknown_path_is_challenged_rather_than_missing()
    {
        // The third endpoint nobody wrote. A fallback policy is evaluated even
        // when routing matched nothing, so an anonymous request for a path
        // that does not exist is a 401 rather than a 404, accepted on §11.2's
        // terms: a caller with no credentials learns nothing about which paths
        // this service has, which is the same argument the 405 pair carries.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/v1/no-such-path", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_path_is_a_404_to_a_caller()
    {
        // The half that makes the one above an assertion about authorization
        // rather than about routing: with a caller, the same request is the
        // 404 it always was. Without this, "401" would pass just as happily
        // against a host that had stopped routing altogether.
        using AuthenticatedUnreachableFactory authenticated = new();
        using HttpClient client = authenticated.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/no-such-path");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response =
            await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
