using System.Net;
using System.Net.Http.Json;
using Catalog.TestSupport;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>Claims only a host keeping the production authentication scheme can make.</summary>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate()
    {
        // This host registers only the production JWT scheme, so TestAuthHandler's headers must be just bytes. No
        // Authorization header, so nothing is fetched from the authority.
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
        // Keeps the test above from passing on a host that refuses everything; §10.2 makes this route anonymous.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }
}
