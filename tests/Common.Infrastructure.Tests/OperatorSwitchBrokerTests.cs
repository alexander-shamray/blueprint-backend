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

    private ServiceProvider Build(
        OperatorSwitchTests.MutableSource source,
        string queue = RabbitFixture.Queue,
        string? routedThrough = null)
    {
        Uri broker = new(fixture.BrokerAddress);
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
                cfg.Host(broker);
                cfg.ConnectOperatorSwitch(
                    context,
                    topology => Bus.Factory.CreateUsingRabbitMq(d =>
                    {
                        d.Host(broker);
                        topology(d);
                    }));
                cfg.SwitchedReceiveEndpoint(
                    context,
                    queue,
                    e =>
                    {
                        // Binding this test's message-type exchange is outside payments-svc's grant (ADR-036).
                        e.ConfigureConsumeTopology = false;
                        if (routedThrough is not null)
                            ((IRabbitMqReceiveEndpointConfigurator)e).Bind(routedThrough);
                        e.ConfigureConsumer<BrokerProbeConsumer>(context);
                    });
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task A_message_published_while_the_endpoint_is_off_waits_in_the_queue_and_drains_when_it_is_on()
    {
        string queue = RabbitFixture.Queue;
        OperatorSwitchTests.MutableSource source = new();
        await using ServiceProvider provider = Build(source);
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

    [Fact]
    public async Task A_host_started_with_its_switch_off_consumes_none_of_its_backlog_until_switched_on()
    {
        // A pod restarting during an incident: the queue already holds messages and the switch is still off. The
        // endpoint is never configured, so the broker has no consumer to deliver any of them to (ADR-144).
        string queue = RabbitFixture.Queue;
        OperatorSwitchTests.MutableSource source = new();
        source.Set(queue);
        Guid[] backlog = [.. Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7())];

        // Kept until the test ends, since MassTransit's log context is static and would outlive a disposed host; its
        // own source, so turning the second host's switch on cannot restart this one's endpoint.
        OperatorSwitchTests.MutableSource firstSource = new();
        firstSource.Set(queue);
        await using ServiceProvider first = Build(firstSource);
        IBusControl bus = first.GetRequiredService<IBusControl>();
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => first.GetRequiredService<ReceiveEndpointSwitch>().IsStopped(queue));
            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{queue}"));
            foreach (Guid id in backlog)
                await endpoint.Send(new BrokerProbe(id), TestContext.Current.CancellationToken);
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        await using ServiceProvider provider = Build(source);
        IBusControl restarted = provider.GetRequiredService<IBusControl>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => endpoints.IsStopped(queue));
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            backlog.ShouldNotContain(
                id => BrokerProbeConsumer.Seen.Contains(id),
                "nothing is consumed while the switch is off, not even the batch a start used to take");

            source.Set();
            await Until(() => backlog.All(BrokerProbeConsumer.Seen.Contains));
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_message_routed_to_an_endpoint_off_since_start_waits_in_its_queue_and_drains_when_on()
    {
        // A queue and a binding the broker has never seen, as a release adding a consumer brings: unconfigured, the
        // endpoint declared neither, so the message routed nowhere and was lost without an alert (ADR-145).
        string queue = $"payments-operator-switch-deferred-{Guid.NewGuid():N}";
        string exchange = $"payments-operator-switch-routed-{Guid.NewGuid():N}";
        OperatorSwitchTests.MutableSource source = new();
        source.Set(queue);
        await using ServiceProvider provider = Build(source, queue, exchange);
        IBusControl bus = provider.GetRequiredService<IBusControl>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        await bus.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Until(() => endpoints.TopologyPending.Count == 0);

            Guid routed = Guid.CreateVersion7();
            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"exchange:{exchange}"));
            await endpoint.Send(new BrokerProbe(routed), TestContext.Current.CancellationToken);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            BrokerProbeConsumer.Seen.ShouldNotContain(routed, "declaring the topology must not start consuming");
            endpoints.IsStopped(queue).ShouldBeTrue();

            source.Set();
            await Until(() => BrokerProbeConsumer.Seen.Contains(routed));
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }
    }
}
