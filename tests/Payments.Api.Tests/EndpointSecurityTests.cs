using System.Net;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// What only a host running the PRODUCTION authentication scheme can say.
/// Every other suite here swaps in <see cref="TestAuthHandler"/>, which is
/// what lets them authenticate at all and exactly why none of them can answer
/// this. It honours the promise <c>UnreachableInfrastructureFactory</c>
/// carries — its <c>ConfigureAuthentication</c> override stays deliberately
/// empty — and sits apart from the class owning that factory because it names
/// <c>/v1/payments</c>: the factory is copied to every new service, where this
/// file belongs to the slice and is written once per slice instead.
/// </summary>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_read_path()
    {
        // The headers are TestAuthHandler's own, and this host registers only
        // the production JWT scheme, so what every other suite here
        // authenticates with is just bytes. The failure it catches is a test
        // convenience reaching production wiring — a scheme registered in
        // Common.Web for the fixtures, or this factory's empty
        // ConfigureAuthentication override deleted as dead code — after which
        // any caller could name any subject and any permission while every
        // authorization test still passed. No Authorization header at all, so
        // nothing is fetched from the authority: .invalid never resolves.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, $"/v1/payments/{Guid.CreateVersion7()}");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, PaymentsPermissions.Admin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_liveness_probe_still_answers_without_a_token()
    {
        // What keeps the test above from passing for the wrong reason: if this
        // host were simply refusing everything, it would be 401 whether or not
        // authentication worked. Payments has no anonymous endpoint of its own
        // — what it holds for an order is never public — so the liveness probe
        // is the one unauthenticated path there is, and §13.5 requires it to
        // answer for the orchestrator rather than for a caller.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
