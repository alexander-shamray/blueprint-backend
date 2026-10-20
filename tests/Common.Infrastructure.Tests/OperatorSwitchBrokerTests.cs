using System.Collections.Concurrent;
using Common.Infrastructure.Switching;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary><see cref="ReceiveEndpointSwitch"/> over a real broker, where the queue persists and a start
/// rebinds.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OperatorSwitchBrokerTests(RabbitFixture fixture)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public sealed record BrokerProbe(Guid Id);

    public sealed class BrokerProbeConsumer : IConsumer<BrokerProbe>
    {
        public static readonly ConcurrentBag<Guid> Seen = [];

        public Task Consume(ConsumeContext<BrokerProbe> context)
        {
            Seen.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Budget);
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task A_message_published_while_the_endpoint_is_off_waits_in_the_queue_and_drains_when_it_is_on()
    {
        string queue = RabbitFixture.Queue;
        OperatorSwitchTests.MutableSource source = new();
        IConfiguration configuration = new ConfigurationBuilder().Add(source).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddOperatorSwitches(configuration);
        services.AddMassTransit(x =>
        {
            x.AddConsumer<BrokerProbeConsumer>();
            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(fixture.BrokerAddress));
                cfg.ConnectOperatorSwitch(context);
                cfg.ReceiveEndpoint(
                    queue,
                    e =>
                    {
                        // Binding this test's message-type exchange is outside payments-svc's grant (ADR-036).
                        e.ConfigureConsumeTopology = false;
                        e.ConfigureConsumer<BrokerProbeConsumer>(context);
                    });
            });
        });

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        IBusControl bus = provider.GetRequiredService<IBusControl>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => provider.GetRequiredService<OperatorSwitches>().Endpoints.Contains(queue));
            await Until(() => bus.CheckHealth().Status == BusHealthStatus.Healthy);

            source.Set(queue);
            await Until(() => endpoints.IsStopped(queue));
            bus.CheckHealth().Status.ShouldBe(
                BusHealthStatus.Degraded,
                "Unhealthy would unready the pod, and a switched-off pod is meant to stay in service");

            Guid waiting = Guid.CreateVersion7();
            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{queue}"));
            await endpoint.Send(new BrokerProbe(waiting), TestContext.Current.CancellationToken);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            BrokerProbeConsumer.Seen.ShouldNotContain(
                waiting,
                "a stopped endpoint takes no message, or the switch is a label");

            source.Set();
            await Until(() => !endpoints.IsStopped(queue));
            await Until(() => BrokerProbeConsumer.Seen.Contains(waiting));
            bus.CheckHealth().Status.ShouldBe(BusHealthStatus.Healthy);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }
}
