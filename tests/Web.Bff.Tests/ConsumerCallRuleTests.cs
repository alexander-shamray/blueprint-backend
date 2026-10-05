using Common.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-017's rule over this host's composition: a consumer's synchronous call is declared (§9.7).</summary>
public class ConsumerCallRuleTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    /// <summary>The host as <c>Program</c> composes it, captured before the base factory replaces anything.</summary>
    public sealed class ComposedFactory()
        : BffFactory
    {
        private IReadOnlyList<ServiceDescriptor> _composition = [];

        public IReadOnlyList<ServiceDescriptor> Composition
        {
            get
            {
                _ = Services;
                return _composition;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services => _composition = [.. services]);
            base.ConfigureWebHost(builder);
        }
    }

    private static readonly System.Reflection.Assembly Host = typeof(Program).Assembly;

    [Fact]
    public void Every_synchronous_call_a_consumer_reaches_is_declared()
    {
        // The pricing hop is an endpoint's (§9.7), so the projection's consumers reach none of these.
        ConsumerCallRule.Offenders(factory.Composition, Host, []).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_consumers_and_clients()
    {
        // The floor: an offender list is as green over an empty composition.
        ConsumerCallRule.Consumers(factory.Composition, Host).ShouldNotBeEmpty();

        // Named, so a client this host gains is seen here before a consumer can reach it.
        Names(ConsumerCallRule.Clients(factory.Composition, Host)).ShouldBe(
            ["CachingTokenClient", "HttpClient", "PricingClient"]);
    }

    private static string[] Names(IEnumerable<Type> types) => [.. types.Select(type => type.Name)];
}
