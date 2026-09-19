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

/// <summary>
/// The harness smoke: <c>AddMassTransitTestHarness</c> replaces an existing
/// <c>AddMassTransit</c> bus with the in-memory transport, so these tests
/// prove the production helper composes (its eager key read runs, its
/// options land, nothing conflicts with the consumer bindings) and that
/// MassTransit's pipeline delivers. What the swap removes is the
/// <c>UsingRabbitMq</c> transport configuration itself, asserted instead
/// against a real broker in the container suite's readiness poll. The
/// message and consumer are test-local, needing only a payload to carry.
/// </summary>
public class MessagingRegistrationTests
{
    /// <summary>
    /// The bus never dials this — the harness swaps the transport before
    /// start — but the helper's eager read still requires a value, and an
    /// unresolvable one means a test that accidentally reaches for the real
    /// transport fails loudly (§12.4's <c>.invalid</c> convention).
    /// </summary>
    private static IConfiguration Configuration(
        string? rabbitConnectionString = "amqp://guest:guest@payments-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    /// <summary>
    /// The bound that decides the assertions below, stated rather than
    /// inherited: it runs from the last bus activity, and MassTransit's
    /// default of 1.2 seconds is a developer machine's budget, not a
    /// saturated CI runner's. 30 s is generous enough for a smoke that
    /// asserts only positives and so never waits it out, while still
    /// failing a genuine composition defect in one bounded wait.
    /// </summary>
    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The harness's other bound, stated for the same reason and
    /// deliberately larger: an assertion ends at the earliest applicable
    /// bound, not the inactivity one alone, and leaving this one inherited
    /// would let a number the test never states decide the wait. 60 s
    /// rather than a matching 30 s so it never fires first — equal values
    /// would leave the two bounds racing, and which one failed would be a
    /// detail of how long the publish took.
    /// </summary>
    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    /// <summary>
    /// One registration, shared by the smoke and by the guard that asserts
    /// its timeout, deliberately: a guard building its own harness would
    /// keep passing with <c>SetTestTimeouts</c> deleted from the smoke,
    /// precisely the deletion it exists to catch. <c>SetTestTimeouts</c>
    /// comes first because it is the only call in the chain returning
    /// <c>IBusRegistrationConfigurator</c>; <c>AddConsumer&lt;T&gt;</c>
    /// returns a consumer configurator, so the other order does not compile.
    /// </summary>
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
        // The defect this replaced was invisible from the smoke below: with
        // SetTestTimeouts deleted that test still passes on an idle machine
        // and fails only on a loaded runner, so a deletion would come back as
        // a flake rather than as a red test. Asserted here it fails at once —
        // as does a MassTransit bump that stops honouring the call.
        //
        // Both bounds, because the wait ends at whichever fires first: pinning
        // only the inactivity one would leave the other free to drop below it
        // and cap the wait without anything here going red.
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
        // Descriptors, not a built provider: building would start nothing
        // (the bus starts with the host), but a provider is a heavier claim
        // than the test makes.
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
        // §3.2's Consumes column for Payments. A consumer registered and
        // never bound looks exactly like one that was never added, and this
        // is the half of that pair a harness-swapped registration can see —
        // the binding is a separate claim, provable only against a real queue.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        foreach (Type consumer in new[]
                 {
                     typeof(IntegrationEventConsumer<OrderPlaced>),
                     typeof(IntegrationEventConsumer<OrderCancelled>)
                 })
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
            d => d.ImplementationType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>)
                 || d.ServiceType == typeof(CommandConsumer<AuthorisePayment, AuthorisePaymentCommand>),
            "AuthorisePayment is §3.2's Accepts column and has no AddConsumer");
    }

    [Fact]
    public void The_ladder_is_non_decreasing_and_starts_under_a_minute()
    {
        RedeliveryLadder.Intervals.ShouldBe(RedeliveryLadder.Intervals.Order());
        RedeliveryLadder.Intervals[0].ShouldBeLessThan(TimeSpan.FromMinutes(1),
            "the routine reorder is milliseconds; the first wait should not cost the saga minutes");
    }

    [Fact]
    public void Usage_telemetry_is_disabled_by_the_production_registration_alone()
    {
        // Deliberately no harness: AddMassTransitTestHarness disables usage
        // telemetry itself (verified in the 8.5.3 source), so a harness-backed
        // assertion would stay green with the production line deleted — and
        // every real host would quietly resume reporting to the vendor.
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
        // Eager, like AddSqlServer one folder over (§13.5): read lazily inside
        // UsingRabbitMq, the missing key would surface at bus start — after
        // the host is up, past ValidateOnBuild, in a background service's log.
        // Blank rows because an empty environment variable configures an empty
        // string, which a null-only guard waves through.
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            services.AddMassTransitMessaging(Configuration(rabbitConnectionString: value)));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }
}
