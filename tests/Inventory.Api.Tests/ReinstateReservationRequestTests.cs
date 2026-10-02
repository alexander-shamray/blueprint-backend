using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>What the host answers a reinstatement whose body never binds, with no store to reach.</summary>
public class ReinstateReservationRequestTests(HostSmokeTests.AuthenticatedUnreachableFactory factory)
    : IClassFixture<HostSmokeTests.AuthenticatedUnreachableFactory>
{
    [Fact]
    public async Task A_body_that_does_not_bind_is_400_before_the_pipeline()
    {
        using HttpClient client = factory.CreateClient();
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
                $"'{body}' got past binding, and the pipeline on this host can only fail on an unreachable store");

            string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using JsonDocument problem = JsonDocument.Parse(content);
            problem.RootElement.TryGetProperty("errors", out _).ShouldBeFalse(
                $"'{body}' was refused by the validator, so it bound and reached the pipeline");
            (problem.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null).ShouldBe(
                "request.unreadable",
                $"'{body}' was refused by something other than binding (§10.5)");
        }
    }
}
