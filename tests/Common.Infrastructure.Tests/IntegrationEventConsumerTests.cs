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

/// <summary>§9.4's broker adapter, through the in-memory harness because handler resolution is under test.</summary>
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

    /// <summary>A second registration for one event, so "every handler runs" is a claim.</summary>
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
            // Explicitly, since the scan's own coverage is asserted where the scan lives.
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

        // §9.1's one identity: without the callback MassTransit mints its own header and nothing here fails.
        await harness.Bus.Publish(
            new ProbeEvent { MessageId = messageId, CorrelationId = messageId, OccurredAt = Now },
            c =>
            {
                c.MessageId = messageId;
                c.CorrelationId = messageId;
            },
            TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<ProbeEvent>(TestContext.Current.CancellationToken)).ShouldBeTrue();

        // One inbox row covers both handlers (§9.5).
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

        // §9.4: an ack would let the inbox filter commit its row and suppress the message for good.
        IReceivedMessage<UnhandledEvent> received = await harness.Consumed
            .SelectAsync<UnhandledEvent>(TestContext.Current.CancellationToken)
            .FirstOrDefault();

        received.ShouldNotBeNull();
        received.Exception
            .ShouldBeOfType<InvalidOperationException>()
            .Message.ShouldContain("No IIntegrationEventHandler<UnhandledEvent> is registered");
    }

    [Fact]
    public async Task The_delivery_lag_is_measured_from_the_messages_own_timestamp()
    {
        // Three seconds of travel: the lag is the consumer's clock less OccurredAt (§13.3).
        FakeTimeProvider clock = new(Now.AddSeconds(3));
        await using ServiceProvider provider = BuildProvider(clock);

        using RecordedMeasurements measurements =
            new(provider.GetRequiredService<IMeterFactory>(), "Commerce.Messaging");

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
