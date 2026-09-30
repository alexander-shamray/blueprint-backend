using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Messaging;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Persistence;
using System.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MassTransit.Middleware;
using MassTransit.Middleware.Outbox;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The production helper composes under the in-memory transport the harness swaps in.</summary>
public class MessagingRegistrationTests
{
    /// <summary>Unresolvable, so a test reaching for the real transport fails (§12.4).</summary>
    private static IConfiguration Configuration(
        string? rabbitConnectionString = "amqp://guest:guest@ordering-rabbit.invalid:5672") =>
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
            "the transport configuration itself is DatabaseSmokeTests' claim, not this one's, and both " +
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
    public void Ordering_registers_exactly_the_consumers_its_chapters_grant()
    {
        // The set §3.2 gives Ordering a consumer for; the saga's events arrive through its own correlation and
        // register no IConsumer<>, and the harness hides endpoints, so this is the registration alone.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        ConsumerTypes(services).ShouldBe(
            [
                // Catalog's Publishes column, the price projection's feed (§3.2).
                typeof(IntegrationEventConsumer<ProductPublished>),
                typeof(IntegrationEventConsumer<PriceChanged>),
                typeof(IntegrationEventConsumer<ProductDiscontinued>),

                // Recorded on the aggregate as well as read by the saga through its own correlation.
                typeof(IntegrationEventConsumer<StockReserved>),

                // §3.2's Accepts column: a type missing here is sent into a queue that ignores it.
                typeof(CommandConsumer<CancelOrder, CancelOrderCommand>),
                typeof(CommandConsumer<ConfirmOrder, ConfirmOrderCommand>),
                typeof(CommandConsumer<MarkOrderShipped, MarkOrderShippedCommand>),
                typeof(CommandConsumer<FlagOrderForReview, FlagOrderForReviewCommand>)
            ],
            ignoreOrder: true,
            "these eight are every event and command §3.2 gives Ordering a CONSUMER for — a ninth is a " +
            "subscription no chapter grants, and a missing one is a handler that silently stops being " +
            "invoked. §3.2's Consumes column is longer: the seven fulfilment events reach the saga through " +
            "its own correlation rather than through an IConsumer<>, which is why they are absent here " +
            "and asserted by the harness suite instead");
    }

    [Fact]
    public void The_saga_is_registered_with_a_scheduler_behind_it()
    {
        // The container half of ADR-021; cfg.UseDelayedMessageScheduler cannot be read from a ServiceCollection.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(
            d => d.ServiceType == typeof(IMessageScheduler),
            "§9.6's Schedule declarations need one, and nothing resolves a scheduler at startup — " +
            "without this line the first OrderPlaced faults onto the error queue (ADR-021)");

        services.ShouldContain(
            d => (d.ImplementationType ?? d.ServiceType) == typeof(OrderFulfilmentSaga),
            "the state machine itself — AddSagaStateMachine registers the machine as well as its instance");
    }

    [Fact]
    public void The_saga_has_a_transactional_outbox_rather_than_an_in_memory_one()
    {
        // ADR-032's container half: the factory the endpoint filter resolves, closed over this service's context.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldContain(
            d => d.ServiceType == typeof(IOutboxContextFactory<OrderingDbContext>),
            "AddEntityFrameworkOutbox<OrderingDbContext> is what registers it, and nothing else here " +
            "does — its absence puts §9.6's saga back on the in-memory outbox (#128, ADR-032)");
    }

    [Fact]
    public void The_outbox_transaction_is_serializable_because_the_saga_repository_stopped_owning_it()
    {
        // Under ADR-032 the outbox opens the transaction the repository joins, and MassTransit defaults it to
        // RepeatableRead; only Serializable takes the key-range lock two first deliveries need.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        using ServiceProvider provider = services.BuildServiceProvider();
        EntityFrameworkOutboxOptions<OrderingDbContext> options = provider
            .GetRequiredService<IOptions<EntityFrameworkOutboxOptions<OrderingDbContext>>>()
            .Value;

        options.IsolationLevel.ShouldBe(
            IsolationLevel.Serializable,
            "the outbox filter opens the consume transaction and the saga repository joins it, so " +
            "this value — not ConcurrencyMode.Pessimistic — is what serialises two deliveries " +
            "racing to create one instance (ADR-032)");
    }

    [Fact]
    public void The_outbox_columns_survive_this_contexts_string_convention()
    {
        // §7.2's 400-character cap is cleared on MassTransit's columns by an internal helper, so it is asserted.
        // The model, not the database, so this fails without a container.
        ServiceCollection services = new();

        services.AddDbContext<OrderingDbContext>(o => o.UseSqlServer("Server=.;Database=probe"));

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        IModel model = scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Model;

        IEntityType message = model.GetEntityTypes()
            .Single(t => t.ClrType.FullName == "MassTransit.EntityFrameworkCoreIntegration.OutboxMessage");

        foreach (string column in (string[])["Body", "Headers", "Properties", "MessageType"])
        {
            message.FindProperty(column)!.GetMaxLength().ShouldBeNull(
                $"{column} carries a serialised message and must stay nvarchar(max); this context's " +
                "400-character convention would truncate it, and MassTransit clears that convention " +
                "through an internal helper rather than by setting a length here (ADR-032)");
        }

        message.FindProperty("DestinationAddress")!.GetMaxLength().ShouldBe(
            256,
            "the addresses are the columns MassTransit does bound, and a 400 here would mean the " +
            "convention won after all");
    }

    [Fact]
    public void The_bus_side_outbox_is_deliberately_not_registered()
    {
        // UseBusOutbox() would stage the request path §9.4's outbox already owns (ADR-032).
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        services.ShouldNotContain(
            d => d.ServiceType == typeof(IBusOutboxNotification),
            "UseBusOutbox() would register this. §9.4's outbox owns the request path, and a second " +
            "stage on a path with no dual write is cost without a guarantee (ADR-032)");
    }

    /// <summary>The consumer types registered, by what they implement, since the service type is the class.</summary>
    private static Type[] ConsumerTypes(IServiceCollection services) =>
        [.. services
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Where(t => Array.Exists(
                t.GetInterfaces(),
                i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>)))
            .Distinct()];

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
