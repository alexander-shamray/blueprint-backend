using Common.Infrastructure.Outbox;
using Common.Infrastructure.Switching;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-113: nothing this host receives or runs is left without a switch.</summary>
public class OperatorSwitchRegistrationTests
{
    [Fact]
    public async Task Every_receive_endpoint_the_host_declares_is_reachable_by_a_switch()
    {
        // Unresolvable, and never started: building the bus configures its endpoints and connects to nothing.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                [new("ConnectionStrings:RabbitMq", "amqp://guest:guest@notifications-rabbit.invalid:5672")])
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddMassTransitMessaging(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        _ = provider.GetRequiredService<IBus>();

        provider.GetRequiredService<OperatorSwitches>().Endpoints.ShouldBe(
            [DependencyInjection.EventsQueue, DependencyInjection.PrivacyQueue],
            "a receive endpoint added to this host must be reachable by a switch, and this list names it");

        provider.GetRequiredService<ReceiveEndpointSwitch>().Unregistered.ShouldBeEmpty(
            "an endpoint not configured through SwitchedReceiveEndpoint consumes at start while it is off");
    }

    [Fact]
    public void Every_worker_loop_the_host_runs_is_a_switched_worker()
    {
        Type[] loops =
        [
            .. typeof(DependencyInjection).Assembly.GetTypes()
                .Concat(typeof(OutboxDispatcher).Assembly.GetTypes())
                .Where(t => !t.IsAbstract && typeof(BackgroundService).IsAssignableFrom(t))
        ];

        loops.ShouldNotBeEmpty("the predicate must see this host's loops, or the assertion below proves nothing");
        loops.ShouldAllBe(
            t => typeof(ISwitchedWorker).IsAssignableFrom(t),
            "a loop that is not an ISwitchedWorker cannot be switched off by an operator (ADR-113)");
    }
}
