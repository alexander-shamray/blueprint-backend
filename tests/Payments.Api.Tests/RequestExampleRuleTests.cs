using System.Net;
using System.Text.Json;
using Payments.TestSupport;
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

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
    public void The_rule_above_is_looking_at_the_bodies_this_host_binds()
    {
        // The floor: this host binds no body yet, so the rule above is vacuous. The day one is mapped this test
        // fails; name it here, which is what keeps a vacuous gate from quietly becoming a permanent one.
        RequestExampleRule.Bodied(Endpoints).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_served_document_carries_an_example_on_every_request_body()
    {
        using JsonDocument document = await DocumentAsync();

        // Counted against the endpoint table, so a document that lost its bodies is not read as clean.
        RequestExampleRule.Bodies(document).Count.ShouldBe(RequestExampleRule.Bodied(Endpoints).Count);
        RequestExampleRule.Unexampled(document).ShouldBeEmpty();
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
}
