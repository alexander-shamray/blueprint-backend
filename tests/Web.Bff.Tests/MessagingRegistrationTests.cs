using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Web.Bff.Messaging;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The production helper composes under the in-memory transport the harness swaps in.</summary>
public sealed class MessagingRegistrationTests
{
    /// <summary>ADR-051's eight events, the BFF's row in §3.2's Consumes column.</summary>
    public static readonly Type[] Consumed =
    [
        typeof(OrderPlaced),
        typeof(OrderConfirmed),
        typeof(OrderCancelled),
        typeof(PaymentAuthorised),
        typeof(PaymentRefunded),
        typeof(ShipmentDispatched),
        typeof(ShipmentDelivered),
        typeof(ProductPublished)
    ];

    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    private static IConfiguration Configuration(string? rabbitConnectionString = BffFactory.UnreachableBroker) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task Publish_reaches_a_consumer_with_the_transport_swapped_for_in_memory()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());
        services.AddMassTransitTestHarness(x => x
            .SetTestTimeouts(HarnessTestTimeout, HarnessInactivityTimeout)
            .AddConsumer<ProbeConsumer>());

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        await harness.Bus.Publish(new ProbeMessage(id), TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the helper's registrations did not compose with the consumer bindings");
    }

    [Fact]
    public void Registration_adds_the_bus_and_its_hosted_service()
    {
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(d => d.ServiceType == typeof(IBus));
        services.ShouldContain(
            d => d.ServiceType == typeof(IHostedService),
            "MassTransit starts the bus from a hosted service; without it the registration is inert");
    }

    [Fact]
    public void Every_event_in_the_bff_row_is_registered()
    {
        // Registered only; the binding is a separate claim, provable only against a real queue.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        foreach (Type consumer in Consumed.Select(e => typeof(IntegrationEventConsumer<>).MakeGenericType(e)))
        {
            services.ShouldContain(
                d => d.ImplementationType == consumer || d.ServiceType == consumer,
                $"{consumer.Name} is in the BFF's Consumes cell and has no AddConsumer");
        }
    }

    [Fact]
    public void Usage_telemetry_is_disabled_by_the_production_registration_alone()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        provider
            .GetRequiredService<IOptions<UsageTelemetryOptions>>()
            .Value.Enabled.ShouldBeFalse("§13.2 owns this platform's telemetry, and none of it leaves silently");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_or_blank_connection_string_fails_at_registration_naming_the_key(string? value)
    {
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: value)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
