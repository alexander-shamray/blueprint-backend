using System.Net;
using System.Net.Http.Json;
using Catalog.TestSupport;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The two things only a host running the PRODUCTION authentication scheme can
/// say. Every other suite here swaps in <see cref="TestAuthHandler"/>, which is
/// what lets them authenticate at all and exactly why none of them can answer
/// either question.
/// </summary>
/// <remarks>
/// Separate from <c>HostSmokeTests</c>, which owns the factory these share, and
/// separate for the scaffold's sake: both tests name <c>/v1/catalog/products</c>,
/// so both belong to the slice and neither survives into a service that has no
/// endpoints. <c>HostSmokeTests</c> is copied to every new service and this file
/// is not.
/// </remarks>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate()
    {
        // Appendix C's first security deliverable: a forged header without a
        // token is a 401. The headers are TestAuthHandler's own, and this host
        // registers only the production JWT scheme, so they are just bytes
        // here. The failure it catches is a test convenience reaching
        // production wiring — a scheme registered in Common.Web "for the
        // fixtures", or a ConfigureAuthentication override deleted as dead
        // code — after which every authorization test in the repository still
        // passes and any caller can name any subject and any permission. No
        // Authorization header at all, so nothing is fetched from the
        // authority: .invalid never resolves, and a challenge needs no keys.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(
                new
                {
                    CommandId = Guid.CreateVersion7(),
                    Name = "Walnut desk",
                    ThumbnailUrl = (string?)null,
                    Amount = 10m,
                    Currency = "EUR"
                })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_public_listing_needs_no_token()
    {
        // The other half, and what keeps the test above from passing for the
        // wrong reason: if authentication were simply refusing everything, both
        // would be 401. This endpoint is AllowAnonymous by §10.2's
        // catalog-public route, so it reaches the handler and fails on the
        // unreachable database instead — a statement about this host's
        // infrastructure, not about its authorization.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }
}
