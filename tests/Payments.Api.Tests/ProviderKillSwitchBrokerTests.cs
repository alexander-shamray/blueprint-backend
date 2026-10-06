using MassTransit;
using Payments.Application.Provider;
using Payments.Infrastructure.Messaging;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary><see cref="ProviderKillSwitch"/> over a real broker: it stops an endpoint, then restarts it.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ProviderKillSwitchBrokerTests(ServiceFixture fixture)
{
    /// <summary>Under <c>payments-svc</c>'s prefix, the only names that account may declare (ADR-036).</summary>
    private const string Queue = "payments-kill-switch-probe";

    /// <summary>Enough to activate the switch, every one faulting.</summary>
    private const int Burst = ProviderKillSwitch.ActivationThreshold + 1;

    /// <summary>How long a step is given past its own timeout, generous for a loaded runner.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public sealed record Probe(Guid Id);

    private sealed class UnavailableConsumer : IConsumer<Probe>
    {
        public Task Consume(ConsumeContext<Probe> context) =>
            throw new PaymentProviderUnavailableException("The provider did not answer.");
    }

    private sealed class Transitions : IReceiveEndpointObserver
    {
        private int _ready;

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Restarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready(ReceiveEndpointReady ready)
        {
            if (Interlocked.Increment(ref _ready) > 1)
                Restarted.TrySetResult();

            return Task.CompletedTask;
        }

        public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;

        public Task Completed(ReceiveEndpointCompleted completed)
        {
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_providers_unavailability_stops_the_endpoint_and_RestartTimeout_starts_it_again()
    {
        Transitions transitions = new();
        IBusControl bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.BrokerAddress));
            cfg.ReceiveEndpoint(
                Queue,
                e =>
                {
                    // Binding this test's message-type exchange is outside payments-svc's grant (ADR-036).
                    e.ConfigureConsumeTopology = false;
                    e.UseKillSwitch(ProviderKillSwitch.Configure);
                    e.Consumer<UnavailableConsumer>();
                    e.ConnectReceiveEndpointObserver(transitions);
                });
        });

        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{Queue}"));
            for (int i = 0; i < Burst; i++)
                await endpoint.Send(new Probe(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

            await transitions.Stopped.Task.WaitAsync(Budget, TestContext.Current.CancellationToken);
            bus.CheckHealth().Status.ShouldBe(
                BusHealthStatus.Degraded,
                "a stopped endpoint must leave readiness answering 200, or a provider outage unreadies the host");

            await transitions.Restarted.Task.WaitAsync(
                ProviderKillSwitch.RestartTimeout + Budget,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }
}
