using Catalog.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// The harness smoke of Appendix C's messaging row, and the row's own split
/// says which half lives here: "publish/consume proven with the in-memory
/// harness" is this file, "bus connects" belongs where a real broker answers.
/// <c>AddMassTransitTestHarness</c> swaps the bus for the in-memory
/// transport, so what these prove is that the production helper composes and
/// that the pipeline delivers — not the swapped-out <c>UsingRabbitMq</c>.
/// </summary>
/// <remarks>The message and consumer are test-local: this smoke needs a
/// payload the pipeline can carry, not a published contract.</remarks>
public class MessagingRegistrationTests
{
    /// <summary>
    /// The bus never dials this — the harness swaps the transport before
    /// start — but the helper's eager read still requires a value, and an
    /// unresolvable one means a test that accidentally reaches for the real
    /// transport fails loudly (§12.4's <c>.invalid</c> convention).
    /// </summary>
    private static IConfiguration Configuration(
        string? rabbitConnectionString = "amqp://guest:guest@catalog-rabbit.invalid:5672") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(rabbitConnectionString is null
                ? []
                : [new KeyValuePair<string, string?>("ConnectionStrings:RabbitMq", rabbitConnectionString)])
            .Build();

    /// <summary>
    /// The bound that decides the assertions below, stated rather than
    /// inherited. It runs from the last bus activity, and MassTransit's
    /// default is 1.2 seconds — a developer machine's budget, not a statement
    /// about how long a saturated runner may take to schedule a consumer.
    /// </summary>
    /// <remarks>
    /// CI runs seven test assemblies concurrently, three of them starting
    /// Testcontainers, on two cores, and this test failed there and passed on
    /// a re-run of the same commit with no changes. 30 s is a generous
    /// scheduling budget for a smoke that asserts only positives and so never
    /// waits it out, while still failing a genuine composition defect in one
    /// bounded wait rather than hanging.
    /// </remarks>
    private static readonly TimeSpan HarnessInactivityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The harness's other bound, stated for the same reason and deliberately
    /// larger. An assertion ends at the <em>earliest</em> applicable bound,
    /// not at the inactivity one alone — verified at the 8.5.3 pin, where
    /// <c>testTimeout: 2 s</c> against <c>testInactivityTimeout: 10 s</c> gave
    /// up after 2 s, and where this timeout is measured from the
    /// <c>Any(…)</c> call rather than from harness start. Leaving it inherited
    /// would let a number the test never states decide the wait, which is the
    /// defect this file exists to fix, one parameter over.
    /// </summary>
    /// <remarks>
    /// 60 s rather than a matching 30 s so that it never fires first: equal
    /// values would leave the two bounds racing, and which one reported a
    /// failure would be a detail of how long the publish took.
    /// </remarks>
    private static readonly TimeSpan HarnessTestTimeout = TimeSpan.FromSeconds(60);

    public sealed record ProbeMessage(Guid Id);

    public sealed class ProbeConsumer : IConsumer<ProbeMessage>
    {
        public Task Consume(ConsumeContext<ProbeMessage> context) => Task.CompletedTask;
    }

    /// <summary>
    /// One registration, shared by the smoke and by the guard that asserts its
    /// timeout — and shared deliberately. A guard building its own harness
    /// would keep passing with <c>SetTestTimeouts</c> deleted from the smoke,
    /// which is precisely the deletion it exists to catch.
    /// </summary>
    /// <remarks>
    /// <c>SetTestTimeouts</c> comes first because it is the only call in the
    /// chain returning <c>IBusRegistrationConfigurator</c>;
    /// <c>AddConsumer&lt;T&gt;</c> returns a consumer configurator, so the
    /// other order does not compile.
    /// </remarks>
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
        // Not visible from the smoke below: with
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
            "the transport configuration itself is DatabaseSmokeTests' claim, not this one's, and both " +
            "harness bounds are stated rather than inherited, so a busy runner is not the answer");
    }

    [Fact]
    public void Registration_adds_the_bus_and_its_hosted_service()
    {
        // Descriptors, not a built provider. Building would start nothing
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
    public void The_consumer_assertion_can_actually_see_a_consumer()
    {
        // The positive control for any assertion built on IsConsumerRegistration:
        // MassTransit's AddConsumer<T> registers the consumer's concrete type,
        // not an implementation of IConsumer<T>, so a predicate matching on the
        // interface finds nothing whether or not a consumer is present — the
        // fail-open shape this repository watches for. Deliberately NOT through
        // AddMassTransitMessaging: that helper calls AddMassTransit itself, and
        // MassTransit permits exactly one such call per container.
        ServiceCollection services = new();

        services.AddMassTransit(x => x.AddConsumer<ProbeConsumer>());

        services.ShouldContain(
            d => IsConsumerRegistration(d),
            "if this cannot see a consumer that IS registered, an assertion built on the predicate proves nothing");
    }

    /// <summary>
    /// A registration MassTransit made for a consumer. The implementation type
    /// is what carries the interface — the service type is the consumer class
    /// itself — so this asks what the registered type implements rather than
    /// what it is registered as.
    /// </summary>
    internal static bool IsConsumerRegistration(ServiceDescriptor descriptor)
    {
        Type? candidate = descriptor.ImplementationType ?? descriptor.ServiceType;

        return candidate is not null &&
            Array.Exists(
                candidate.GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));
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
