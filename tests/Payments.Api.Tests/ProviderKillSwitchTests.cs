using Common.Application;
using MassTransit;
using Payments.Application;
using Payments.Application.Provider;
using Payments.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary><see cref="ProviderKillSwitch"/> at the pinned MassTransit, on an endpoint shaped as Payments'.</summary>
public sealed class ProviderKillSwitchTests
{
    private const string Queue = "kill-switch-probe";

    /// <summary>Immediate, so a message's ladder is milliseconds; the retries still precede its fault.</summary>
    private const int Retries = 5;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public enum Fault
    {
        None,
        Unavailable,
        Mismatch,
        Mapping
    }

    public sealed record Probe(Guid Id, Fault Fault);

    /// <summary>Every call into the consumer, a retry's as much as a first delivery's.</summary>
    private sealed class Attempts
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    private sealed class ProbeConsumer(Attempts attempts) : IConsumer<Probe>
    {
        public Task Consume(ConsumeContext<Probe> context)
        {
            attempts.Add();

            return context.Message.Fault switch
            {
                Fault.Unavailable => throw new PaymentProviderUnavailableException("The provider did not answer."),
                Fault.Mismatch => throw new PaymentMismatchException("A reused key with different figures."),
                Fault.Mapping => throw new ContractMappingException("An unmappable message."),
                _ => Task.CompletedTask
            };
        }
    }

    private sealed class StopObserver : IReceiveEndpointObserver
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready(ReceiveEndpointReady ready) => Task.CompletedTask;

        public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;

        public Task Completed(ReceiveEndpointCompleted completed)
        {
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;
    }

    private sealed class ConsumedObserver : IConsumeObserver
    {
        private int _settled;
        private int _faults;

        public int Settled => Volatile.Read(ref _settled);

        public int Faults => Volatile.Read(ref _faults);

        public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

        public Task PostConsume<T>(ConsumeContext<T> context) where T : class
        {
            Interlocked.Increment(ref _settled);
            return Task.CompletedTask;
        }

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        {
            Interlocked.Increment(ref _faults);
            Interlocked.Increment(ref _settled);
            return Task.CompletedTask;
        }
    }

    private static IBusControl Bus(StopObserver stops, ConsumedObserver consumed, Attempts attempts)
    {
        IBusControl bus = MassTransit.Bus.Factory.CreateUsingInMemory(cfg =>
        {
            cfg.ReceiveEndpoint(
                Queue,
                e =>
                {
                    e.UseKillSwitch(ProviderKillSwitch.Configure);
                    e.UseMessageRetry(r =>
                    {
                        r.Ignore<ContractMappingException>();
                        r.Ignore<PaymentMismatchException>();
                        r.Immediate(Retries);
                    });
                    e.Consumer(() => new ProbeConsumer(attempts));
                    e.ConnectReceiveEndpointObserver(stops);
                });
        });
        bus.ConnectConsumeObserver(consumed);

        return bus;
    }

    private static async Task SendAsync(IBusControl bus, Fault fault, int count)
    {
        ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{Queue}"));
        for (int i = 0; i < count; i++)
            await endpoint.Send(new Probe(Guid.CreateVersion7(), fault), TestContext.Current.CancellationToken);
    }

    /// <summary>Enough to activate the switch, each faulting after its whole ladder.</summary>
    private const int Burst = ProviderKillSwitch.ActivationThreshold + 1;

    [Fact]
    public async Task The_providers_unavailability_trips_it_after_each_message_has_spent_its_retries()
    {
        StopObserver stops = new();
        ConsumedObserver consumed = new();
        Attempts attempts = new();
        IBusControl bus = Bus(stops, consumed, attempts);
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await SendAsync(bus, Fault.Unavailable, Burst);

            await stops.Stopped.Task.WaitAsync(Budget, TestContext.Current.CancellationToken);

            // What TrackingPeriod relies on: the switch counts a message once, after its whole ladder.
            attempts.Count.ShouldBe(Burst * (Retries + 1), "every message ran its first delivery and every retry");
            consumed.Faults.ShouldBe(Burst, "each message faulted once, after its retries, not once per attempt");

            bus.CheckHealth().Status.ShouldBe(
                BusHealthStatus.Degraded,
                "a stopped endpoint must leave readiness answering 200, or a provider outage unreadies the host");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(Fault.Mismatch)]
    [InlineData(Fault.Mapping)]
    public async Task A_terminal_fault_does_not_trip_it(Fault fault)
    {
        StopObserver stops = new();
        ConsumedObserver consumed = new();
        IBusControl bus = Bus(stops, consumed, new Attempts());
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Twice the burst that trips it on unavailability, then a message that must still be consumed.
            await SendAsync(bus, fault, Burst * 2);
            await SendAsync(bus, Fault.None, 1);

            using CancellationTokenSource deadline = new(Budget);
            while (consumed.Settled < (Burst * 2) + 1 && !deadline.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

            consumed.Settled.ShouldBe((Burst * 2) + 1, "every message settles on an endpoint the switch left open");
            stops.Stopped.Task.IsCompleted.ShouldBeFalse($"a {fault} fault is the message's defect, not an outage");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }
}
