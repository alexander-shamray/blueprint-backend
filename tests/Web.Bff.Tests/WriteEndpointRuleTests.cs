using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(BffFactory factory) : IClassFixture<BffFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        // No assembly to scan: this host has no Application layer, so it declares no command (§4.1).
        WriteEndpointRule.Offenders(Endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(["Quote"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    [Fact]
    public void The_order_reads_are_in_the_table_and_outside_the_writes()
    {
        string[] mapped = Names(Endpoints);

        // In the table the rule reads, so leaving them out of Writes is the rule's judgement and not a blind spot.
        mapped.ShouldContain("ListOrders");
        mapped.ShouldContain("GetOrder");
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(["Quote"]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
