using Shipping.TestSupport;
using Common.TestSupport;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>ADR-017's rule over this host's composition: a consumer's synchronous call is declared (§9.7).</summary>
public class ConsumerCallRuleTests(ConsumerCallRuleTests.ComposedFactory factory)
    : IClassFixture<ConsumerCallRuleTests.ComposedFactory>
{
    /// <summary>The host as <c>Program</c> composes it, captured before the base factory replaces anything.</summary>
    public sealed class ComposedFactory()
        : ShippingWorkerFactory(HostSmokeTests.UnreachableSql, HostSmokeTests.UnreachableRabbit)
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
        // The address read and the carrier's calls are workers' (ADR-052), so no consumer spends an exception.
        ConsumerCallRule.Offenders(factory.Composition, Host, []).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_consumers_and_clients()
    {
        // The floor: an offender list is as green over an empty composition.
        ConsumerCallRule.Consumers(factory.Composition, Host).ShouldNotBeEmpty();

        // Named, so a client this host gains is seen here before a consumer can reach it.
        Names(ConsumerCallRule.Clients(factory.Composition, Host)).ShouldBe(
            [
                "CachingTokenClient",
                "DeliveryAddressesClient",
                "GrpcDeliveryAddressSource",
                "HttpClient",
                "ICarrierGateway"
            ]);
    }

    public sealed record Probe;

    // Generic, because the Ordering stub this suite references declares a client of the same name.
    public sealed class Addressing<TClient>(TClient client) : IConsumer<Probe>
        where TClient : class
    {
        public TClient Client { get; } = client;

        public Task Consume(ConsumeContext<Probe> context) => Task.CompletedTask;
    }

    private Type AddressClient =>
        factory.Composition.First(d => d.ServiceType.Name == "DeliveryAddressesClient").ServiceType;

    private IServiceCollection AddressingComposition()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddScoped(typeof(Addressing<>).MakeGenericType(AddressClient));
        foreach (ServiceDescriptor descriptor in factory.Composition)
        {
            if (descriptor.ServiceType == AddressClient)
                services.Add(descriptor);
        }

        return services;
    }

    [Fact]
    public void A_consumer_taking_a_registered_client_is_reported_under_the_clients_type()
    {
        ConsumerCallRule.Offenders(AddressingComposition(), typeof(ConsumerCallRuleTests).Assembly, [])
            .ShouldHaveSingleItem()
            .ShouldContain("> reaches DeliveryAddressesClient (Addressing<DeliveryAddressesClient> -> ");
    }

    [Fact]
    public void An_exception_on_the_clients_type_grants_a_consumer_that_takes_it()
    {
        ConsumerCallException granted = new(
            AddressClient,
            GrantedBy: "ADR-052",
            WhenUnreachable: "the message is retried",
            WhenAnsweredNo: "the message faults",
            UnreachableChoice.Correctness);

        ConsumerCallRule.Offenders(AddressingComposition(), typeof(ConsumerCallRuleTests).Assembly, [granted])
            .ShouldBeEmpty();
    }

    private static string[] Names(IEnumerable<Type> types) => [.. types.Select(type => type.Name)];
}
