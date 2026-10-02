using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>§10.5's unreadable-request row through the composed host, with no store to reach.</summary>
public class UnreadableBodyTests(HostSmokeTests.AuthenticatedUnreachableFactory factory)
    : IClassFixture<HostSmokeTests.AuthenticatedUnreachableFactory>
{
    [Theory]
    [InlineData("Development", """{"onHand":"three"}""", "#/onHand")]
    [InlineData("Production", """{"onHand":"three"}""", "#/onHand")]
    [InlineData("Development", """{"onHand":3,""", null)]
    [InlineData("Production", """{"onHand":3,""", null)]
    [InlineData("Development", null, null)]
    [InlineData("Production", null, null)]
    public async Task A_stock_take_whose_body_does_not_bind_is_the_same_400_in_every_environment(
        string environment,
        string? body,
        string? expected)
    {
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

        HttpResponseMessage response = await client.PutAsync(
            $"/v1/inventory/stock/{Guid.CreateVersion7()}",
            body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("request.unreadable");
        problem.RootElement.TryGetProperty("errors", out _).ShouldBeFalse("the body never reached the validator");

        if (expected is null)
            problem.RootElement.TryGetProperty("pointer", out _).ShouldBeFalse();
        else
            problem.RootElement.GetProperty("pointer").GetString().ShouldBe(expected);
    }
}
