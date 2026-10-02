using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

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
            .Offenders(Endpoints, typeof(Notifications.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Every_idempotent_command_is_made_of_members_its_fingerprint_sees()
    {
        // A member the fingerprint does not see lets a different request replay as the first (ADR-057).
        CommandFingerprintRule
            .Offenders(typeof(Notifications.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void This_host_maps_no_write_for_the_rule_above_to_look_at_yet()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBeEmpty(
            "This host maps no write endpoint yet, so the rule above is vacuous. The day it maps " +
            "one, this test fails — replace it with the ShouldBe form naming that endpoint, " +
            "which is what keeps a vacuous gate from quietly becoming a permanent one (§8.5).");

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
