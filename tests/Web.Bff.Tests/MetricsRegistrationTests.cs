using Common.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Web.Bff.Observability;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§13.6's registration rules over the services <c>Program</c> composes, every extension it calls.</summary>
public sealed class MetricsRegistrationTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    // The framework registers meters of its own (Hosting, Kestrel, Routing); §13.6 binds this platform's, by exclusion
    // so that a type in any other namespace is still seen.
    private Type[] Registered() =>
    [
        .. factory.Composition
            .Select(d => d.ServiceType)
            .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
            .Where(t => t.Namespace is not null && !t.Namespace.StartsWith("Microsoft.", StringComparison.Ordinal))
            .Distinct()
    ];

    [Fact]
    public void Every_metrics_type_is_forced_by_the_initialiser()
    {
        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
        ];

        // Both directions: unforced is an instrument that may never exist, forced-but-unregistered a host that
        // will not start.
        Registered().ShouldBe(forced, ignoreOrder: true);
    }

    [Fact]
    public void The_metrics_selector_actually_selects_something()
    {
        Registered().ShouldContain(typeof(MessagingMetrics));
        Registered().ShouldContain(typeof(ProjectionMetrics));
    }

    [Fact]
    public void The_initialiser_is_registered_as_a_hosted_service() =>
        factory.Composition
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ShouldContain(typeof(MetricsInitialiser));
}
