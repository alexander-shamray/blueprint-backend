using Common.Infrastructure.Switching;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-113's switch: a stopped endpoint consumes nothing and a started one drains its queue.</summary>
public class OperatorSwitchTests
{
    private const string Queue = "switch-probe";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public sealed record Probe(Guid Id);

    public sealed class ProbeConsumer : IConsumer<Probe>
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<Guid> Seen = [];

        public Task Consume(ConsumeContext<Probe> context)
        {
            Seen.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>A provider whose contents the test replaces, which is what a reloaded file does.</summary>
    private sealed class MutableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Set(params string[] off)
        {
            Data = off.Select((name, i) => (Key: $"{OperatorSwitchOptions.SectionName}:Off:{i}", Value: name))
                .ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    private static ServiceProvider Build(MutableSource source)
    {
        IConfiguration configuration = new ConfigurationBuilder().Add(source).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddOperatorSwitches(configuration);
        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<ProbeConsumer>();
            x.UsingInMemory((context, cfg) =>
            {
                cfg.ConnectOperatorSwitch(context);
                cfg.ReceiveEndpoint(Queue, e => e.ConfigureConsumer<ProbeConsumer>(context));
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Budget);
        while (!condition())
            await Task.Delay(25, timeout.Token);
    }

    [Fact]
    public async Task A_switched_off_endpoint_consumes_nothing_and_switching_it_on_drains_the_queue()
    {
        MutableSource source = new();
        await using ServiceProvider provider = Build(source);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        await harness.Start();
        await Until(() => provider.GetRequiredService<OperatorSwitches>().Endpoints.Contains(Queue));

        source.Set(Queue);
        await Until(() => endpoints.IsStopped(Queue));
        Guid waiting = Guid.CreateVersion7();
        await harness.Bus.Publish(new Probe(waiting), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        harness.Consumed.Select<Probe>(m => m.Context.Message.Id == waiting, TestContext.Current.CancellationToken)
            .Any().ShouldBeFalse(
            "a stopped endpoint takes no message, or the switch is a label");

        source.Set();
        await Until(() => !endpoints.IsStopped(Queue));
        await Until(() => ProbeConsumer.Seen.Contains(waiting));
    }

    [Fact]
    public async Task An_endpoint_started_while_its_switch_is_off_is_stopped()
    {
        MutableSource source = new();
        source.Set(Queue);
        await using ServiceProvider provider = Build(source);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();

        await harness.Start();

        await Until(() => endpoints.IsStopped(Queue));
    }

    [Fact]
    public async Task A_name_no_endpoint_or_worker_declares_is_reported_rather_than_ignored()
    {
        MutableSource source = new();
        await using ServiceProvider provider = Build(source);
        await provider.GetRequiredService<ITestHarness>().Start();

        source.Set("no-such-endpoint", Queue);

        provider.GetRequiredService<OperatorSwitches>().Undeclared.ShouldBe(["no-such-endpoint"]);
    }

    [Fact]
    public async Task A_stopped_endpoint_leaves_the_bus_Degraded_which_readiness_answers_200()
    {
        MutableSource source = new();
        await using ServiceProvider provider = Build(source);
        await provider.GetRequiredService<ITestHarness>().Start();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        IBusControl bus = provider.GetRequiredService<IBusControl>();
        await Until(() => bus.CheckHealth().Status == BusHealthStatus.Healthy);

        source.Set(Queue);
        await Until(() => endpoints.IsStopped(Queue));

        bus.CheckHealth().Status.ShouldBe(
            BusHealthStatus.Degraded,
            "Unhealthy would unready the pod, and a switched-off pod is meant to stay in service");
    }
}
