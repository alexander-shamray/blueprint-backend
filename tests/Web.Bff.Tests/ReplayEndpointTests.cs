using BffReplay;
using MassTransit;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The inbox key a reset deletes, held to the one the BFF's own filter writes (§9.5).</summary>
public sealed class ReplayEndpointTests
{
    public static TheoryData<string, string> Brokers => new()
    {
        { "amqp://rabbit:5672", "bff-order-events" },
        { "amqp://rabbit:5672/", "bff-order-events" },
        { "amqp://rabbit:5672/%2F", "bff-order-events" },
        { "amqp://rabbit:5672/shop", "shop/bff-order-events" }
    };

    [Theory]
    [MemberData(nameof(Brokers))]
    public void The_inbox_key_is_the_queue_under_the_vhost_the_broker_address_names(string broker, string expected) =>
        ProjectionReset.EndpointFor(broker, Replay.Queue).ShouldBe(expected);

    [Theory]
    [MemberData(nameof(Brokers))]
    public void The_inbox_key_is_the_path_masstransit_gives_a_receive_address_of_that_broker(string broker, string _)
    {
        IBusControl bus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(broker)));

        // A receive address is the host's path and the queue's name, and InboxFilter keys on that path.
        string host = bus.Address.AbsolutePath;
        string receivePath = host[..(host.LastIndexOf('/') + 1)] + Replay.Queue;

        ProjectionReset.EndpointFor(broker, Replay.Queue).ShouldBe(receivePath.TrimStart('/'));
    }
}
