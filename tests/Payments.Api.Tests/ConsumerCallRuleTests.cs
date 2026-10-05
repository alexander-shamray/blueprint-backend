using Common.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Provider;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>ADR-017's rule over this host's composition: a consumer's synchronous call is declared (§9.7).</summary>
public class ConsumerCallRuleTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    /// <summary>The host as <c>Program</c> composes it, captured before the base factory replaces anything.</summary>
    public sealed class ComposedFactory()
        : PaymentsApiFactory(HostSmokeTests.UnreachableSql, HostSmokeTests.UnreachableRabbit)
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

    /// <summary>The provider call ADR-070 grants, inside the consumers that authorise and cancel.</summary>
    private static readonly ConsumerCallException ProviderCall = new(
        typeof(IPaymentProvider),
        GrantedBy: "ADR-070",
        WhenUnreachable: "the message is retried and then faulted, and ProviderKillSwitch stops the endpoint; " +
            "no verdict is recorded",
        WhenAnsweredNo: "a decline is recorded and PaymentDeclined staged",
        UnreachableChoice.Correctness);

    private static readonly System.Reflection.Assembly Host = typeof(Program).Assembly;

    [Fact]
    public void Every_synchronous_call_a_consumer_reaches_is_declared()
    {
        ConsumerCallRule.Offenders(factory.Composition, Host, [ProviderCall]).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_consumers_and_clients()
    {
        // The floor: an offender list is as green over an empty composition.
        ConsumerCallRule.Consumers(factory.Composition, Host).ShouldNotBeEmpty();

        // Named, so a client this host gains is seen here before a consumer can reach it.
        Names(ConsumerCallRule.Clients(factory.Composition, Host)).ShouldBe(["HttpClient", "IPaymentProvider"]);
    }

    [Fact]
    public void Without_its_declaration_the_provider_call_is_an_offender()
    {
        // The handlers are reached through the dispatcher, so this fails if the walk stops at it.
        ConsumerCallRule
            .Offenders(factory.Composition, Host, [])
            .ShouldContain(offender => offender.Contains("reaches IPaymentProvider", StringComparison.Ordinal));
    }

    private static string[] Names(IEnumerable<Type> types) => [.. types.Select(type => type.Name)];
}
