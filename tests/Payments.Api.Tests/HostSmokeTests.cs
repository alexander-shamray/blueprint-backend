using System.Net;
using Payments.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// The host builds under <c>ValidateOnBuild</c> and answers what an empty
/// service can already be asked: the probes (§13.5) and the OpenAPI document
/// (Appendix C, PR-07). One factory for the class — each test sends one
/// request, and a host per test buys nothing.
/// </summary>
/// <remarks>
/// Both connection strings are required rather than optional, and supplying
/// them is not a workaround. §13.5's rule is that a host with a connection
/// string has a readiness check and a host without one does not; this
/// service has both pairs from its first commit, and both
/// registrations throw on a missing key — so a service host with no database
/// or no broker configured does not start, which is the correct behaviour and
/// worth having a class depend on. The values point at names that cannot
/// resolve, because these tests are about wiring rather than about the
/// engines. <c>DatabaseSmokeTests</c> is where real ones answer.
/// </remarks>
public class HostSmokeTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    /// <summary>
    /// A parameterless factory, because that is what <c>IClassFixture</c> can
    /// construct. <c>.invalid</c> is reserved and never resolves, so both
    /// checks fail on NXDOMAIN rather than on a timeout — and
    /// <c>Connect Timeout=1</c> bounds the case where a resolver answers
    /// anyway. The bus needs no such bound: <c>WaitUntilStarted</c> is false
    /// (the registration argues it), so the host never waits on the broker at
    /// all.
    /// </summary>
    // The two literals both factories below take. Declared once because they
    // are the same host under two authentication schemes, and a pair that
    // drifted would make the two suites disagree about which deployment they
    // are describing.
    private const string UnreachableSql =
        "Server=payments-sql.invalid,1433;Database=Payments;User Id=sa;" +
        "Password=not-a-real-password;Encrypt=False;Connect Timeout=1";

    private const string UnreachableRabbit = "amqp://guest:guest@payments-rabbit.invalid:5672";

    /// <summary>
    /// The same unreachable host with the <c>TestAuthHandler</c> scheme the
    /// base factory installs, so a caller can authenticate.
    /// </summary>
    /// <remarks>
    /// It exists because <c>AddCommonWebDefaults</c> sets a fallback
    /// authorization policy (§11.4): the OpenAPI document is behind it, so the
    /// production-scheme factory can prove only that a caller is challenged.
    /// Whether the document still generates needs a caller who gets through,
    /// and this is the cheapest one — no container, since generating the
    /// document reaches no dependency.
    /// </remarks>
    public sealed class AuthenticatedUnreachableFactory()
        : PaymentsApiFactory(UnreachableSql, UnreachableRabbit);

    public sealed class UnreachableInfrastructureFactory()
        : PaymentsApiFactory(UnreachableSql, UnreachableRabbit)
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

        // Registered by AddMassTransit itself, not by AddPaymentsInfrastructure
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
        // when routing matched NOTHING, so an anonymous request for a path
        // that does not exist is a 401 rather than a 404 — measured, and
        // accepted on §11.2's terms: a caller with no credentials learns
        // nothing about which paths this service has, which is the same
        // argument the 405 pair and the OpenAPI document already carry.
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

    [Fact]
    public async Task OpenApi_document_is_not_anonymous()
    {
        // MapOpenApi carries no authorization metadata of its own, so it is
        // reached by the fallback policy AddCommonWebDefaults sets (§11.4).
        // That is the decision rather than an accident: the document
        // enumerates every route and every schema this service has, and §11.2
        // assumes the network inside the cluster is hostile.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OpenApi_document_is_served_to_a_caller()
    {
        // The half a 401 cannot show. Without this the test above would go on
        // passing if the document stopped generating altogether — every path
        // answers 401 to an anonymous caller, the ones that do not exist
        // included.
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
