using Payments.Application.Intents.AuthorisePayment;
using Payments.Infrastructure.Messaging;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>The production helper composes under the in-memory transport the harness swaps in.</summary>
public class MessagingRegistrationTests
{
    /// <summary>Unresolvable, so a test reaching for the real transport fails (§12.4).</summary>
    private static IConfiguration Configuration(
        string? rabbitConnectionString = "amqp://guest:guest@payments-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    /// <summary>Stated, since MassTransit's 1.2-second default is a developer machine's budget.</summary>
    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Larger than the inactivity bound, so the two never race to end a wait.</summary>
    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    /// <summary>Shared by the smoke and the guard on its timeouts, so the guard sees the smoke's own.</summary>
    private static ServiceProvider BuildHarnessProvider()
    {
        ServiceCollection services = new();
        services.AddMassTransitMessaging(Configuration());
        services.AddMassTransitTestHarness(x => x
            .SetTestTimeouts(HarnessTestTimeout, HarnessInactivityTimeout)
            .AddConsumer<ProbeConsumer>());

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task The_harness_waits_for_the_stated_timeouts_rather_than_MassTransits_defaults()
    {
        // Both bounds, because the wait ends at whichever fires first; a deleted SetTestTimeouts fails here at once.
        await using ServiceProvider provider = BuildHarnessProvider();

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();

        harness.TestInactivityTimeout.ShouldBe(
            HarnessInactivityTimeout,
            "this is the bound that normally decides an unmatched assertion — 1.2s is MassTransit's " +
            "default and a developer machine's budget, not a saturated two-core runner's");
        harness.TestTimeout.ShouldBe(
            HarnessTestTimeout,
            "the assertion ends at the earliest applicable bound, so a TestTimeout below the " +
            "inactivity timeout would silently become the wait");
    }

    [Fact]
    public async Task Publish_reaches_a_consumer_with_the_transport_swapped_for_in_memory()
    {
        await using ServiceProvider provider = BuildHarnessProvider();

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var id = Guid.CreateVersion7();
        await harness.Bus.Publish(new ProbeMessage(id), TestContext.Current.CancellationToken);

        (await harness.Published.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await harness.Consumed.Any<ProbeMessage>(
            m => m.Context.Message.Id == id,
            TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the harness replaced the RabbitMQ transport, so a message that publishes but is never " +
            "consumed means the helper's registrations did not compose with the consumer bindings — " +
            "the transport configuration itself is the container suite's claim, not this one's, and both " +
            "harness bounds are stated rather than inherited, so a busy runner is not the answer");
    }

    [Fact]
    public void Registration_adds_the_bus_and_its_hosted_service()
    {
        // Descriptors, not a built provider, which is a heavier claim than this test makes.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(d => d.ServiceType == typeof(IBus));
        services.ShouldContain(
            d => d.ServiceType == typeof(IHostedService),
            "MassTransit starts the bus from a hosted service; without it the registration is inert");
    }

    [Fact]
    public void Every_event_in_the_consumes_column_is_registered()
    {
        // §3.2's Consumes column for Payments; the binding is a separate claim, provable only against a real queue.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        Type[] consumers =
        [
            typeof(IntegrationEventConsumer<OrderPlaced>),
            typeof(IntegrationEventConsumer<OrderCancelled>)
        ];
        foreach (Type consumer in consumers)
        {
            services.ShouldContain(
                d => d.ImplementationType == consumer || d.ServiceType == consumer,
                $"{consumer.Name} is in §3.2's Consumes column and has no AddConsumer");
        }
    }

    [Fact]
    public void Every_command_in_the_accepts_column_is_registered()
    {
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(
            d => d.ImplementationType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>) ||
                d.ServiceType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>),
            "AuthorisePayment is §3.2's Accepts column and has no AddConsumer");
    }

    [Fact]
    public void The_ladder_is_non_decreasing_and_starts_under_a_minute()
    {
        RedeliveryLadder.Intervals.ShouldBe(RedeliveryLadder.Intervals.Order());
        RedeliveryLadder.Intervals[0].ShouldBeLessThan(
            TimeSpan.FromMinutes(1),
            "the routine reorder is milliseconds; the first wait should not cost the saga minutes");
    }

    [Fact]
    public void Usage_telemetry_is_disabled_by_the_production_registration_alone()
    {
        // No harness: AddMassTransitTestHarness disables usage telemetry itself, hiding a deleted production line.
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
        // Eager, like AddSqlServer (§13.5), so a missing key fails before bus start; blank rows because an empty
        // environment variable configures an empty string.
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: value)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
