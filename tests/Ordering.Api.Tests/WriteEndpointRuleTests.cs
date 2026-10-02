using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        WriteEndpointRule
            .Offenders(Endpoints, typeof(Ordering.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(
            ["CancelOrder", "PlaceOrder", "gRPC - /ordering.delivery.v1.DeliveryAddresses/Get"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(
            [
                "Health checks",
                "Health checks",
                "Health checks",
                "gRPC - Unimplemented method for ordering.delivery.v1.DeliveryAddresses",
                "gRPC - Unimplemented service"
            ]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
