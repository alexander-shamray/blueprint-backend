using Privacy.TestSupport;
using Common.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>ADR-017's rule over this host's composition: a consumer's synchronous call is declared (§9.7).</summary>
public class ConsumerCallRuleTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    /// <summary>The host as <c>Program</c> composes it, captured before the base factory replaces anything.</summary>
    public sealed class ComposedFactory()
        : PrivacyApiFactory(HostSmokeTests.UnreachableSql, HostSmokeTests.UnreachableRabbit)
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
        ConsumerCallRule.Offenders(factory.Composition, Host, []).ShouldBeEmpty();
    }

    [Fact]
    public void This_host_registers_the_consumer_the_rule_above_looks_at()
    {
        // The floor: an offender list is as green over an empty composition.
        ConsumerCallRule.Consumers(factory.Composition, Host).ShouldNotBeEmpty(
            "the rule above would be vacuous if it looked at no consumer, which is as green as it is clean (ADR-017)");

        // Named, so a client this host gains is seen here before a consumer can reach it.
        Names(ConsumerCallRule.Clients(factory.Composition, Host)).ShouldBe([]);
    }

    private static string[] Names(IEnumerable<Type> types) => [.. types.Select(type => type.Name)];
}
