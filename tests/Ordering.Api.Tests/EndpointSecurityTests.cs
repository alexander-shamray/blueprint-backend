using System.Net;
using System.Net.Http.Json;
using Ordering.Api;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>What only a host on the production scheme can say, the rest using <see cref="TestAuthHandler"/>.</summary>
public class EndpointSecurityTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_write_path()
    {
        // TestAuthHandler's own headers, which are just bytes to the production JWT scheme. With no Authorization
        // header, nothing is fetched from the authority.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(
                new
                {
                    Items = new[] { new { ProductId = Guid.CreateVersion7(), Quantity = 1 } },
                    ShippingAddress = new
                    {
                        Line1 = "1 Test Street",
                        Line2 = (string?)null,
                        City = "Almaty",
                        PostalCode = "050000",
                        Country = "KZ"
                    },
                    Currency = "EUR"
                })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forged_identity_headers_do_not_authenticate_on_the_cancel_path()
    {
        // Both routes, because they carry different policies and a 401 is decided before either is consulted.
        using HttpClient client = factory.CreateClient();

        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{Guid.CreateVersion7()}/cancel")
        {
            Content = JsonContent.Create(new { Reason = "CustomerRequest" })
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Cancel);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_liveness_probe_still_answers_without_a_token()
    {
        // Keeps the two above from passing on a host that refuses everything.
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
