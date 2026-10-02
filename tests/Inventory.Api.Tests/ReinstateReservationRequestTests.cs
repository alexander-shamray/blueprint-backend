using System.Net;
using System.Text;
using Inventory.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>What the deployed host answers a reinstatement whose body never binds, with no store to reach.</summary>
public class ReinstateReservationRequestTests(HostSmokeTests.AuthenticatedUnreachableFactory factory)
    : IClassFixture<HostSmokeTests.AuthenticatedUnreachableFactory>
{
    [Fact]
    public async Task A_body_that_does_not_bind_is_400_before_the_pipeline()
    {
        // Production, since Development's RouteHandlerOptions.ThrowOnBadRequest raises the same refusal as an
        // exception no §10.5 handler translates.
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using HttpClient client = production.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

        string?[] bodies = [null, "", """{"commandId":null}""", """{"commandId":"not-a-guid"}"""];

        foreach (string? body in bodies)
        {
            HttpResponseMessage response = await client.PostAsync(
                $"/v1/inventory/reservations/{Guid.CreateVersion7()}/reinstate",
                body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(
                HttpStatusCode.BadRequest,
                $"'{body}' reached §6.3's pipeline, which on this host can only fail on an unreachable store");
        }
    }
}
