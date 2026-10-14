using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Notifications.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(NotificationsApiFactory factory) : IClassFixture<NotificationsApiFactory>
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
    public void The_rule_above_looks_at_every_write_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(
            [
                "gRPC - /notifications.devices.v1.Devices/Register",
                "gRPC - /notifications.devices.v1.Devices/SignOut",
                "gRPC - /notifications.preferences.v1.Preferences/Get",
                "gRPC - /notifications.preferences.v1.Preferences/Set"
            ]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(
            [
                "Health checks",
                "Health checks",
                "Health checks",
                "gRPC - Unimplemented method for notifications.devices.v1.Devices",
                "gRPC - Unimplemented method for notifications.preferences.v1.Preferences",
                "gRPC - Unimplemented service"
            ]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
