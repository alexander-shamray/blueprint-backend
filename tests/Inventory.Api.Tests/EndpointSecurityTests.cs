using System.Net;
using System.Net.Http.Json;
using Inventory.Api;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// What only a host running the PRODUCTION authentication scheme can say.
/// Every other suite here swaps in <see cref="TestAuthHandler"/>, which is
/// what lets them authenticate at all and exactly why none of them can
/// answer this.
/// </summary>
/// <remarks>
/// <para>
/// Catalog's counterpart is the model, and this file honours the promise
/// <c>UnreachableInfrastructureFactory</c> carries: its
/// <c>ConfigureAuthentication</c> override stays deliberately empty, proved
/// here against a forged-header request.
/// </para>
/// <para>
/// Kept apart from the class that owns the shared factory, and apart for
/// the scaffold's sake: this file names <c>/v1/inventory/stock</c>, so it
/// belongs to the slice and must not survive into a service with no
/// endpoints. The factory is copied to every new service; this file is
/// written once per slice instead.
/// </para>
/// </remarks>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_write_path()
    {
        // The headers are TestAuthHandler's own, which is what makes this
        // worth running: this host registers only the production JWT scheme,
        // so the headers every other suite in this assembly authenticates
        // with are just bytes here.
        //
        // The failure it catches is a test convenience reaching production
        // wiring — a scheme registered in Common.Web "for the fixtures", or
        // this factory's empty ConfigureAuthentication override deleted as
        // dead code. Every authorization test in the repository would still
        // pass, and any caller could name any subject and any permission.
        //
        // No Authorization header at all, so nothing is fetched from the
        // authority — .invalid never resolves, and a challenge needs no
        // signing keys.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Put, $"/v1/inventory/stock/{Guid.CreateVersion7()}")
        {
            Content = JsonContent.Create(new { onHand = 1 })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_read_path()
    {
        // Both routes, though they share one group-level policy: a 401 is
        // decided before it is even consulted, and the write path passing
        // above says nothing about a read path that lost its place in the
        // group RequireAuthorization sits on.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, $"/v1/inventory/stock/{Guid.CreateVersion7()}");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_any_reservation_route()
    {
        // The same forged headers that authenticate against nothing on
        // /v1/inventory/stock authenticate against nothing here either, on
        // the read and both admin actions this group also requires a real
        // token for.
        using HttpClient client = factory.CreateClient();
        var order = Guid.CreateVersion7();

        HttpRequestMessage[] requests =
        [
            new(HttpMethod.Get, $"/v1/inventory/reservations/{order}"),
            new(HttpMethod.Post, $"/v1/inventory/reservations/{order}/release"),
            new(HttpMethod.Post, $"/v1/inventory/reservations/{order}/reinstate")
        ];

        foreach (HttpRequestMessage request in requests)
        {
            request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
            request.Headers.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

            HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, request.RequestUri!.ToString());
        }
    }

    [Fact]
    public async Task The_liveness_probe_still_answers_without_a_token()
    {
        // What keeps the two above from passing for the wrong reason: if this
        // host were simply refusing everything, they would be 401 whether or
        // not authentication worked. Inventory has no anonymous endpoint of
        // its own — a stock level is never public — so the liveness probe is
        // the one unauthenticated path there is, and §13.5 requires it to
        // answer for the orchestrator rather than for a caller.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
