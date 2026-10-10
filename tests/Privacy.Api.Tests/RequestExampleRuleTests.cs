using System.Net;
using System.Text.Json;
using Privacy.Api;
using Privacy.TestSupport;
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>Each body this host binds has an example its validator passes, and the document carries it.</summary>
public class RequestExampleRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_body_this_host_binds_has_an_example_that_holds()
    {
        RequestExampleRule.Offenders(Endpoints, factory.Services).ShouldBeEmpty();
    }

    [Fact]
    public void This_host_binds_the_body_the_rule_above_looks_at()
    {
        Names(RequestExampleRule.Bodied(Endpoints)).ShouldBe(
            ["RaiseErasureRequest"],
            "the rule above would be vacuous if it looked at no body, which is as green as it is clean");
    }

    [Fact]
    public async Task The_served_document_carries_an_example_on_every_request_body()
    {
        using JsonDocument document = await DocumentAsync();

        // Counted against the endpoint table, so a document that lost its bodies is not read as clean.
        RequestExampleRule.Bodies(document).Count.ShouldBe(RequestExampleRule.Bodied(Endpoints).Count);
        RequestExampleRule.Unexampled(document).ShouldBeEmpty();
    }

    [Fact]
    public async Task No_endpoint_refuses_its_own_example()
    {
        using HostSmokeTests.AuthenticatedUnreachableFactory authenticated = new();
        using WebApplicationFactory<Program> observed =
            authenticated.WithWebHostBuilder(web => web.ConfigureTestServices(RequestExampleRule.ObserveValidation));
        using HttpClient client = observed.CreateClient();

        IReadOnlyList<string> refused = await RequestExampleRule.RefusedAsync(
            observed.Services,
            client,
            request =>
            {
                request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
                request.Headers.Add(TestAuthHandler.PermissionsHeader, PrivacyPermissions.Erase);
            },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        refused.ShouldBeEmpty();
    }

    private static async Task<JsonDocument> DocumentAsync()
    {
        using HostSmokeTests.AuthenticatedUnreachableFactory authenticated = new();
        using HttpClient client = authenticated.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/openapi/v1.json");
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
