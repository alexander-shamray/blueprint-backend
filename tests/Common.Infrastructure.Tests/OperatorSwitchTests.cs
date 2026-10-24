using System.Runtime.CompilerServices;
using Common.Infrastructure.Switching;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
    internal sealed class MutableSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Set(params string[] off)
        {
            Data = off.Select((name, i) => (Key: $"{OperatorSwitchOptions.SectionName}:Off:{i}", Value: name))
                .ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    /// <summary>Counts each endpoint the bus readies, which it does only once the transport is consuming.</summary>
    internal sealed class ReadyCounter : IReceiveEndpointObserver
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Readied { get; } = [];

        public Task Ready(ReceiveEndpointReady ready)
        {
            Readied.Add(ready.InputAddress.AbsolutePath.Split('/')[^1]);
            return Task.CompletedTask;
        }

        public Task Stopping(ReceiveEndpointStopping stopping) => Task.CompletedTask;

        public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;

        public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;
    }

    /// <summary>Stands in for the host's topology bus, recording what it is asked to declare; it starts
    /// nothing.</summary>
    internal sealed class TopologyRecorder
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Declared { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<bool> DeployOnly { get; } = [];

        public IBusControl Create(Action<IBusFactoryConfigurator> configure)
        {
            IBusFactoryConfigurator cfg = Substitute.For<IBusFactoryConfigurator>();
            bool deployOnly = false;
            cfg.When(c => c.DeployTopologyOnly = Arg.Any<bool>()).Do(call => deployOnly = call.Arg<bool>());
            cfg.When(c => c.ReceiveEndpoint(Arg.Any<string>(), Arg.Any<Action<IReceiveEndpointConfigurator>>()))
                .Do(call => Declared.Enqueue(call.Arg<string>()));

            configure(cfg);
            DeployOnly.Enqueue(deployOnly);
            return Substitute.For<IBusControl>();
        }
    }

    private static ServiceProvider Build(
        MutableSource source,
        ReadyCounter? counter = null,
        bool plain = false,
        TopologyRecorder? topology = null)
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
                cfg.ConnectOperatorSwitch(context, (topology ?? new TopologyRecorder()).Create);

                void Configure(IReceiveEndpointConfigurator e)
                {
                    if (counter is not null)
                        e.ConnectReceiveEndpointObserver(counter);
                    e.ConfigureConsumer<ProbeConsumer>(context);
                }

                if (plain)
                    cfg.ReceiveEndpoint(Queue, Configure);
                else
                    cfg.SwitchedReceiveEndpoint(context, Queue, Configure);
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
    public async Task An_endpoint_whose_switch_is_off_at_start_is_never_readied_until_it_is_switched_on()
    {
        // Readied means consuming, since the transport starts consuming before it raises Ready; an endpoint stopped
        // from Ready has already taken what the broker sent it.
        MutableSource source = new();
        source.Set(Queue);
        ReadyCounter counter = new();
        await using ServiceProvider provider = Build(source, counter);
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();

        await harness.Start();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        endpoints.IsStopped(Queue).ShouldBeTrue();
        counter.Readied.ShouldNotContain(Queue, "an endpoint off at start is never configured, so never readied");
        provider.GetRequiredService<OperatorSwitches>().Endpoints.ShouldContain(Queue);

        source.Set();
        await Until(() => !endpoints.IsStopped(Queue));
        Guid probe = Guid.CreateVersion7();
        await harness.Bus.Publish(new Probe(probe), TestContext.Current.CancellationToken);
        await Until(() => ProbeConsumer.Seen.Contains(probe));
        await Until(() => counter.Readied.Contains(Queue));
    }

    [Fact]
    public async Task An_endpoint_off_at_start_has_its_topology_declared_by_a_bus_that_only_deploys_it()
    {
        // Unconfigured, the endpoint declares no queue and no binding, so a message routed to it while it is off would
        // reach no queue and be lost (ADR-145).
        MutableSource source = new();
        source.Set(Queue);
        ReadyCounter counter = new();
        TopologyRecorder topology = new();
        await using ServiceProvider provider = Build(source, counter, topology: topology);
        ReceiveEndpointSwitch endpoints = provider.GetRequiredService<ReceiveEndpointSwitch>();
        _ = provider.GetRequiredService<IBus>();
        endpoints.TopologyPending.ShouldBe([Queue], "configuring the bus declares nothing; starting it does");

        await provider.GetRequiredService<ITestHarness>().Start();
        await Until(() => endpoints.TopologyPending.Count == 0);

        topology.Declared.ShouldBe([Queue]);
        topology.DeployOnly.ToArray().ShouldBe(
            [true],
            "a topology bus that also consumed would take what the switch holds");
        endpoints.IsStopped(Queue).ShouldBeTrue();
        counter.Readied.ShouldNotContain(Queue);
    }

    [Fact]
    public async Task An_endpoint_on_at_start_declares_its_own_topology_and_starts_no_topology_bus()
    {
        TopologyRecorder topology = new();
        await using ServiceProvider provider = Build(new MutableSource(), topology: topology);

        await provider.GetRequiredService<ITestHarness>().Start();
        await Task.Delay(500, TestContext.Current.CancellationToken);

        topology.Declared.ShouldBeEmpty();
        provider.GetRequiredService<ReceiveEndpointSwitch>().TopologyPending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_topology_declaration_that_hangs_is_timed_out_and_retried_until_it_lands()
    {
        // A broker unreachable at start must not leave the queue undeclared for the life of the pod.
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int attempts = 0;
        IBusControl Create(Action<IBusFactoryConfigurator> configure)
        {
            IBusControl bus = Substitute.For<IBusControl>();
            if (Interlocked.Increment(ref attempts) == 1)
                bus.StartAsync(Arg.Any<CancellationToken>()).Returns(call => Hang(call.Arg<CancellationToken>()));
            return bus;
        }

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await using ServiceProvider container = new ServiceCollection().BuildServiceProvider();
        endpoints.Attach(container, Create);
        endpoints.Register(Queue, _ => { }).ShouldBeFalse();

        // Started and not awaited, so the sweep returns while its declaration still hangs.
        await endpoints.DeclareDeferredAsync().WaitAsync(Budget, TestContext.Current.CancellationToken);
        Volatile.Read(ref attempts).ShouldBe(1);
        clock.Advance(ReceiveEndpointSwitch.TopologyTimeout);
        endpoints.TopologyPending.ShouldBe([Queue], "a declaration that never finished has declared nothing");

        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return endpoints.TopologyPending.Count == 0;
        });
        Volatile.Read(ref attempts).ShouldBe(2);
        endpoints.IsStopped(Queue).ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_is_not_held_behind_a_topology_declaration_that_hangs()
    {
        // A broker stalling new connections while the bus stays connected: the running endpoint must still stop.
        const string running = "switch-running";
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int attempts = 0;
        IBusControl Create(Action<IBusFactoryConfigurator> configure)
        {
            Interlocked.Increment(ref attempts);
            IBusControl bus = Substitute.For<IBusControl>();
            bus.StartAsync(Arg.Any<CancellationToken>()).Returns(call => Hang(call.Arg<CancellationToken>()));
            return bus;
        }

        int stops = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref stops);
            return Task.CompletedTask;
        });
        ReceiveEndpointReady ready = Substitute.For<ReceiveEndpointReady>();
        ready.InputAddress.Returns(new Uri($"loopback://localhost/{running}"));
        ready.ReceiveEndpoint.Returns(endpoint);

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await using ServiceProvider container = new ServiceCollection().BuildServiceProvider();
        endpoints.Attach(container, Create);
        endpoints.Register(Queue, _ => { }).ShouldBeFalse();
        await endpoints.Ready(ready);

        await endpoints.DeclareDeferredAsync().WaitAsync(Budget, TestContext.Current.CancellationToken);
        Volatile.Read(ref attempts).ShouldBe(1);

        source.Set(Queue, running);
        await Until(() => endpoints.IsStopped(running));
        Volatile.Read(ref stops).ShouldBe(1);

        // A whole sweep, which reaches the deferred queue only after the running one; a declaration starts before
        // the sweep moves on, so a second one would be counted by now.
        await endpoints.ApplyAllAsync().WaitAsync(Budget, TestContext.Current.CancellationToken);
        Volatile.Read(ref attempts).ShouldBe(1, "a declaration already running is not started a second time");
    }

    private static async Task<BusHandle> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable: the delay above never completes");
    }

    [Fact]
    public void An_endpoint_registered_before_the_switch_is_connected_is_refused()
    {
        // Configured would never observe it, so switching it off later would stop nothing while readiness says off.
        MutableSource source = new();
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;
        using ReceiveEndpointSwitch endpoints = new(switches);

        Should.Throw<InvalidOperationException>(() => endpoints.Register(Queue, _ => { }))
            .Message.ShouldContain("before ConnectOperatorSwitch");
        switches.Endpoints.ShouldNotContain(Queue);
    }

    [Fact]
    public async Task An_endpoint_configured_without_the_switch_is_reported_unregistered()
    {
        await using ServiceProvider switched = Build(new MutableSource());
        await switched.GetRequiredService<ITestHarness>().Start();
        switched.GetRequiredService<ReceiveEndpointSwitch>().Unregistered.ShouldNotContain(Queue);

        await using ServiceProvider plain = Build(new MutableSource(), plain: true);
        await plain.GetRequiredService<ITestHarness>().Start();
        plain.GetRequiredService<ReceiveEndpointSwitch>().Unregistered.ShouldContain(Queue);
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

    private static OperatorSwitches SwitchesOver(MutableSource source, out ServiceProvider provider)
    {
        ServiceCollection services = new();
        services.AddOptions<OperatorSwitchOptions>()
            .Bind(new ConfigurationBuilder().Add(source).Build().GetSection(OperatorSwitchOptions.SectionName));
        provider = services.BuildServiceProvider();

        return new OperatorSwitches(provider.GetRequiredService<IOptionsMonitor<OperatorSwitchOptions>>(), provider);
    }

    private static ReceiveEndpointReady ReadyFor(IReceiveEndpoint endpoint)
    {
        ReceiveEndpointReady ready = Substitute.For<ReceiveEndpointReady>();
        ready.InputAddress.Returns(new Uri($"loopback://localhost/{Queue}"));
        ready.ReceiveEndpoint.Returns(endpoint);
        return ready;
    }

    [Fact]
    public async Task A_stop_that_fails_is_retried_until_the_endpoint_stops()
    {
        // No further change may come, and until the stop lands the queue consumes while readiness names it off.
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int stops = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ => Interlocked.Increment(ref stops) == 1
            ? Task.FromException(new InvalidOperationException("the broker refused the stop"))
            : Task.CompletedTask);

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);

        await endpoints.Ready(ReadyFor(endpoint));
        await Until(() => Volatile.Read(ref stops) == 1);
        endpoints.IsStopped(Queue).ShouldBeFalse("the first stop failed");

        // Advanced on every poll, since the retry's timer is armed just after the failure is seen.
        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return endpoints.IsStopped(Queue);
        });
        Volatile.Read(ref stops).ShouldBe(2);
    }

    [Fact]
    public async Task A_start_that_never_becomes_ready_is_stopped_before_it_is_retried()
    {
        // Retrying Start on an endpoint already started would fail every time and never converge.
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int stops = 0;
        int starts = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref stops);
            return Task.CompletedTask;
        });
        endpoint.Start(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ReceiveEndpointHandle handle = Substitute.For<ReceiveEndpointHandle>();
            handle.Ready.Returns(Interlocked.Increment(ref starts) == 1
                ? Task.FromException<ReceiveEndpointReady>(new InvalidOperationException("the broker refused"))
                : Task.FromResult(Substitute.For<ReceiveEndpointReady>()));
            return handle;
        });

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await endpoints.Ready(ReadyFor(endpoint));
        await Until(() => endpoints.IsStopped(Queue));

        source.Set();
        await Until(() => Volatile.Read(ref stops) == 2);
        endpoints.IsStopped(Queue).ShouldBeTrue("the first start never became ready");

        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return !endpoints.IsStopped(Queue);
        });
        Volatile.Read(ref starts).ShouldBe(2);
    }

    [Fact]
    public async Task A_connect_that_never_becomes_ready_is_stopped_and_started_again_never_connected_twice()
    {
        // A second connect would add a second endpoint on the queue while the first, untracked, may still consume.
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int stops = 0;
        int starts = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref stops);
            return Task.CompletedTask;
        });
        endpoint.Start(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref starts);
            ReceiveEndpointHandle started = Substitute.For<ReceiveEndpointHandle>();
            started.Ready.Returns(Task.FromResult(Substitute.For<ReceiveEndpointReady>()));
            return started;
        });
        HostReceiveEndpointHandle connected = Substitute.For<HostReceiveEndpointHandle>();
        connected.ReceiveEndpoint.Returns(endpoint);
        connected.Ready.Returns(
            Task.FromException<ReceiveEndpointReady>(new InvalidOperationException("the broker refused")));
        IReceiveEndpointConnector connector = Substitute.For<IReceiveEndpointConnector>();
        connector
            .ConnectReceiveEndpoint(Queue, Arg.Any<Action<IBusRegistrationContext, IReceiveEndpointConfigurator>>())
            .Returns(connected);
        await using ServiceProvider container = new ServiceCollection().AddSingleton(connector).BuildServiceProvider();

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        endpoints.Attach(container, new TopologyRecorder().Create);
        endpoints.Register(Queue, _ => { }).ShouldBeFalse();

        source.Set();
        await Until(() => Volatile.Read(ref stops) == 1);
        endpoints.IsStopped(Queue).ShouldBeTrue("the connect never became ready");

        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return !endpoints.IsStopped(Queue);
        });
        Volatile.Read(ref starts).ShouldBe(1);
        connector.Received(1).ConnectReceiveEndpoint(
            Queue,
            Arg.Any<Action<IBusRegistrationContext, IReceiveEndpointConfigurator>>());
    }

    [Fact]
    public async Task A_change_while_a_retry_waits_adds_no_second_retry()
    {
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int stops = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref stops);
            return Task.FromException(new InvalidOperationException("the broker refused the stop"));
        });

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await endpoints.Ready(ReadyFor(endpoint));
        await Until(() => Volatile.Read(ref stops) == 1);

        // A change during the fault fails again, and finds the first failure's retry already claimed; awaited, so
        // its retry decision is made before the clock moves.
        await endpoints.ApplyAllAsync();
        Volatile.Read(ref stops).ShouldBe(2);

        // Advanced on every poll, since the call that holds the claim may arm its timer after the one that found it
        // has returned: a single advance in that gap moves the clock past a timer that does not yet exist.
        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return Volatile.Read(ref stops) == 3;
        });
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Volatile.Read(ref stops).ShouldBe(3, "two retry chains would each have stopped again");
    }

    [Fact]
    public async Task A_start_whose_compensating_stop_failed_is_stopped_before_the_next_start()
    {
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;

        int stops = 0;
        int starts = 0;
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ => Interlocked.Increment(ref stops) == 2
            ? Task.FromException(new InvalidOperationException("the broker refused the stop"))
            : Task.CompletedTask);
        endpoint.Start(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ReceiveEndpointHandle handle = Substitute.For<ReceiveEndpointHandle>();
            handle.Ready.Returns(Interlocked.Increment(ref starts) == 1
                ? Task.FromException<ReceiveEndpointReady>(new InvalidOperationException("the broker refused"))
                : Task.FromResult(Substitute.For<ReceiveEndpointReady>()));
            return handle;
        });

        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await endpoints.Ready(ReadyFor(endpoint));
        await Until(() => endpoints.IsStopped(Queue));

        // The start never becomes ready and its compensating stop fails too, so the endpoint may still be running.
        source.Set();
        await Until(() => Volatile.Read(ref stops) == 2);

        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return !endpoints.IsStopped(Queue);
        });
        Volatile.Read(ref stops).ShouldBe(3, "the retry stops the endpoint before it starts it again");
        Volatile.Read(ref starts).ShouldBe(2);
    }

    /// <summary>An endpoint whose stops numbered in <paramref name="failingStops"/> throw, and whose first start never
    /// becomes ready.</summary>
    private static IReceiveEndpoint FaultyEndpoint(
        StrongBox<int> stops,
        StrongBox<int> starts,
        params int[] failingStops)
    {
        IReceiveEndpoint endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(_ =>
            failingStops.Contains(Interlocked.Increment(ref stops.Value))
                ? Task.FromException(new InvalidOperationException("the broker refused the stop"))
                : Task.CompletedTask);
        endpoint.Start(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ReceiveEndpointHandle handle = Substitute.For<ReceiveEndpointHandle>();
            handle.Ready.Returns(Interlocked.Increment(ref starts.Value) == 1
                ? Task.FromException<ReceiveEndpointReady>(new InvalidOperationException("the broker refused"))
                : Task.FromResult(Substitute.For<ReceiveEndpointReady>()));
            return handle;
        });

        return endpoint;
    }

    [Fact]
    public async Task An_unsettled_start_stays_unsettled_until_a_stop_succeeds()
    {
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;
        StrongBox<int> stops = new();
        StrongBox<int> starts = new();

        // The compensating stop fails, and so does the first retry's stop before its start.
        FakeTimeProvider clock = new();
        using ReceiveEndpointSwitch endpoints = new(switches, clock: clock);
        await endpoints.Ready(ReadyFor(FaultyEndpoint(stops, starts, 2, 3)));
        await Until(() => endpoints.IsStopped(Queue));
        source.Set();

        await Until(() =>
        {
            clock.Advance(ReceiveEndpointSwitch.FirstRetry);
            return !endpoints.IsStopped(Queue);
        });
        Volatile.Read(ref stops.Value).ShouldBe(4, "the second retry still stops the endpoint before it starts it");
        Volatile.Read(ref starts.Value).ShouldBe(2);
    }

    [Fact]
    public async Task An_unsettled_endpoint_switched_back_off_is_stopped()
    {
        MutableSource source = new();
        source.Set(Queue);
        OperatorSwitches switches = SwitchesOver(source, out ServiceProvider provider);
        using ServiceProvider _ = provider;
        StrongBox<int> stops = new();
        StrongBox<int> starts = new();

        using ReceiveEndpointSwitch endpoints = new(switches, clock: new FakeTimeProvider());
        await endpoints.Ready(ReadyFor(FaultyEndpoint(stops, starts, 2)));
        await Until(() => endpoints.IsStopped(Queue));
        source.Set();
        await Until(() => Volatile.Read(ref stops.Value) == 2);

        // The set still says stopped, but the failed start may have left the endpoint running.
        source.Set(Queue);
        await endpoints.ApplyAllAsync();

        Volatile.Read(ref stops.Value).ShouldBe(3);
        endpoints.IsStopped(Queue).ShouldBeTrue();
    }
}
