using System.Net;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>The routes driven through the forwarder to a stub, which observes §10.2's prefix strip.</summary>
public sealed class ProxiedRouteTests(StubDestination stub) : IClassFixture<StubDestination>
{
    [Fact]
    public async Task The_service_receives_the_path_with_the_namespace_prefix_removed()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The last path: the stub is a class fixture, and another test sends this same path.
        stub.ReceivedPaths.Last().ShouldBe("/v1/catalog/products");
    }

    /// <summary>Reaching the stub shows <c>catalog-write</c> matched and admitted it (§10.2).</summary>
    [Fact]
    public async Task An_authenticated_post_reaches_catalog_write()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/products");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stub.ReceivedPaths.Last().ShouldBe("/v1/catalog/products");
    }

    /// <summary><c>catalog-write</c> is <c>authenticated</c>, so an anonymous POST reaches nothing (§11.4).</summary>
    [Fact]
    public async Task An_anonymous_post_to_catalog_write_is_refused()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        int before = stub.ReceivedPaths.Count;

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/catalog/products",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        stub.ReceivedPaths.Count.ShouldBe(before);
    }

    [Fact]
    public async Task The_bff_namespace_strips_bff_rather_than_api()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/bff/dashboard");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        stub.ReceivedPaths.Last().ShouldBe("/dashboard");
    }

    /// <summary>The public route names the reserved <c>anonymous</c>, so §11.4's fallback never reaches it.</summary>
    [Fact]
    public async Task The_public_route_forwards_a_caller_carrying_no_token()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response =
            await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>The positive half: a policy refusing everybody passes every negative test (§11.4).</summary>
    [Fact]
    public async Task The_admin_route_admits_a_caller_holding_the_permission()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/inventory/stock");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");
        request.Headers.Add(TestAuthHandler.PermissionsHeader, GatewayPermissions.InventoryAdmin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>The path, not only the status, since the 403 half is answered before YARP forwards anything.</summary>
    [Fact]
    public async Task The_payments_admin_route_forwards_the_stripped_path_to_its_cluster()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        Guid order = Guid.CreateVersion7();

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/payments/{order}");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");
        request.Headers.Add(TestAuthHandler.PermissionsHeader, GatewayPermissions.PaymentsAdmin);

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The order id is fresh, so no other test can have put this path in the stub's log.
        stub.ReceivedPaths.Last().ShouldBe($"/v1/payments/{order}");
    }

    /// <summary>PUT names neither catalog route, so routing answers an authenticated caller 405 (§10.2).</summary>
    /// <remarks>
    /// The anonymous half is <see cref="A_wrong_method_is_challenged_before_it_is_refused"/> (ADR-030).
    /// </remarks>
    [Fact]
    public async Task The_catalog_namespace_matches_no_method_but_get_and_post()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        // Counted, since the stub is a class fixture and other tests send this same path.
        int before = stub.ReceivedPaths.Count;

        using HttpRequestMessage request =
            new(HttpMethod.Put, "/api/v1/catalog/products");
        request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

        HttpResponseMessage response =
            await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        // A 405 minted by the service would read identically, so nothing may reach a destination.
        stub.ReceivedPaths.Count.ShouldBe(before);
    }

    /// <summary>The same without a caller, which §11.4's fallback policy challenges before routing's 405.</summary>
    [Fact]
    public async Task A_wrong_method_is_challenged_before_it_is_refused()
    {
        using StubbedGatewayFactory factory = new(stub.Address);
        using HttpClient client = factory.CreateClient();

        int before = stub.ReceivedPaths.Count;

        HttpResponseMessage response = await client.PutAsync(
            "/api/v1/catalog/products",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        stub.ReceivedPaths.Count.ShouldBe(before);
    }
}
