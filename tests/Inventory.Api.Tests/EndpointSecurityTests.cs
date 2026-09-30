using System.Net;
using System.Net.Http.Json;
using Inventory.Api;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>Claims only a host keeping the production authentication scheme can make.</summary>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_write_path()
    {
        // This host registers only the production JWT scheme, so TestAuthHandler's headers must be just bytes. No
        // Authorization header, so nothing is fetched from the authority.
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
        // Both routes, since the write path passing says nothing about a read path that left the group.
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
        // Keeps the tests above from passing on a host that refuses everything.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
