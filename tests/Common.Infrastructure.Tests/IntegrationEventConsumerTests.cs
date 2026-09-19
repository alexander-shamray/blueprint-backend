using System.Diagnostics.Metrics;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §9.4's adapter from the broker to <c>IIntegrationEventHandler&lt;T&gt;</c>,
/// through the in-memory harness rather than by construction: what is under
/// test is resolution — the closed generic off the container, and the handler
/// collection the §6.2 scan registered.
/// </summary>
/// <remarks>
/// The contract is test-local: a payload carrying the §9.1 envelope, not one
/// other services depend on.
/// </remarks>
public class IntegrationEventConsumerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    public sealed record ProbeEvent : IIntegrationEvent
    {
        public required Guid MessageId { get; init; }

        public required Guid CorrelationId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }

    /// <summary>Bound on the endpoint with nothing registered to handle it.</summary>
    public sealed record UnhandledEvent : IIntegrationEvent
    {
        public required Guid MessageId { get; init; }

        public required Guid CorrelationId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }
    }

    public sealed class FirstHandler : IIntegrationEventHandler<ProbeEvent>
    {
        public static readonly List<Guid> Handled = [];

        public Task HandleAsync(ProbeEvent integrationEvent, CancellationToken ct)
        {
            lock (Handled)
                Handled.Add(integrationEvent.MessageId);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The second registration for one event, which is what makes "every
    /// registered handler runs" a claim rather than a restatement of "the
    /// handler runs".
    /// </summary>
    public sealed class SecondHandler : IIntegrationEventHandler<ProbeEvent>
    {
        public static readonly List<Guid> Handled = [];

        public Task HandleAsync(ProbeEvent integrationEvent, CancellationToken ct)
        {
            lock (Handled)
                Handled.Add(integrationEvent.MessageId);

            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider(FakeTimeProvider clock, bool withHandlers = true)
    {
        ServiceCollection services = new();

        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IMeterFactory, TestMeterFactory>();
        services.AddSingleton<MessagingMetrics>();

        if (withHandlers)
        {
            // Registered explicitly rather than through AddPluggableFrom: the
            // scan's own coverage is asserted where the scan lives, and what
            // this suite is about is what the consumer does with what it is
            // given.
            services.AddScoped<IIntegrationEventHandler<ProbeEvent>, FirstHandler>();
            services.AddScoped<IIntegrationEventHandler<ProbeEvent>, SecondHandler>();
        }

        services.AddMassTransitTestHarness(x =>
        {
            x.SetTestTimeouts(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
            x.AddConsumer<IntegrationEventConsumer<ProbeEvent>>();
            x.AddConsumer<IntegrationEventConsumer<UnhandledEvent>>();
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Every_registered_handler_runs_for_one_message()
    {
        FirstHandler.Handled.Clear();
        SecondHandler.Handled.Clear();

        FakeTimeProvider clock = new(Now);
        await using ServiceProvider provider = BuildProvider(clock);

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();

        // The callback is §9.1's rule, not ceremony: body, row, header and
        // inbox key are ONE GUID, and IIntegrationEvent says the envelope's
        // value is "THE message id, not a second one". Without it MassTransit
        // mints its own header and the event has two identities — and NOTHING
        // FAILS, because every assertion here reads the payload. That is the
        // exact cost that comment predicts, and these three publishes were
        // sitting in it: found while a saga-suite helper was being fixed for
        // the same thing, by a reviewer checking whether the claim "every
        // other publisher writes it" was true. It was not.
        await harness.Bus.Publish(
            new ProbeEvent { MessageId = messageId, CorrelationId = messageId, OccurredAt = Now },
            c =>
            {
                c.MessageId = messageId;
                c.CorrelationId = messageId;
            },
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeEvent>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        // One inbox row covers both (§9.5) precisely because they succeed or
        // fail together, so "both ran" is the behaviour that row is keyed on.
        FirstHandler.Handled.ShouldBe([messageId]);
        SecondHandler.Handled.ShouldBe([messageId]);
    }

    [Fact]
    public async Task Binding_a_type_with_no_handler_faults_the_message_rather_than_acking_it()
    {
        FakeTimeProvider clock = new(Now);
        await using ServiceProvider provider = BuildProvider(clock);

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();
        await harness.Bus.Publish(
            new UnhandledEvent { MessageId = messageId, CorrelationId = messageId, OccurredAt = Now },
            c =>
            {
                c.MessageId = messageId;
                c.CorrelationId = messageId;
            },
            TestContext.Current.CancellationToken);

        // §9.4's "empty is a decision" table: configuring this consumer for the
        // type is a statement that something handles it, and acking would be
        // worse here than anywhere — the inbox filter commits its row once
        // Consume returns, so the message would be suppressed for good.
        IReceivedMessage<UnhandledEvent> received = await harness.Consumed
            .SelectAsync<UnhandledEvent>(TestContext.Current.CancellationToken)
            .FirstOrDefault();

        received.ShouldNotBeNull();
        received.Exception.ShouldBeOfType<InvalidOperationException>()
            .Message.ShouldContain("No IIntegrationEventHandler<UnhandledEvent> is registered");
    }

    [Fact]
    public async Task The_delivery_lag_is_measured_from_the_messages_own_timestamp()
    {
        // Started before the provider, because InstrumentPublished fires at
        // construction and MessagingMetrics is built on first resolution.
        using RecordedMeasurements measurements = new("Commerce.Messaging");

        // Three seconds of travel, with the clock fixed either side of it: the
        // lag is the difference between the consumer's clock and OccurredAt,
        // which is what makes it a cross-machine measurement useful at second
        // granularity and meaningless below it (§13.3).
        FakeTimeProvider clock = new(Now.AddSeconds(3));
        await using ServiceProvider provider = BuildProvider(clock);

        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = Guid.CreateVersion7();
        await harness.Bus.Publish(
            new ProbeEvent { MessageId = messageId, CorrelationId = messageId, OccurredAt = Now },
            c =>
            {
                c.MessageId = messageId;
                c.CorrelationId = messageId;
            },
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeEvent>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        RecordedMeasurements.Measurement lag = measurements.For("messaging.delivery.lag").ShouldHaveSingleItem();

        lag.Value.ShouldBe(3);
        lag.Tag("message").ShouldBe(nameof(ProbeEvent));
    }
}
