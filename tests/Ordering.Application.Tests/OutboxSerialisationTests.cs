using System.Text.Json;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Ordering.Infrastructure;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>The <c>Local</c> lane's payload contract (§9.4), over the registered options as §12.4 argues.</summary>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
    {
        // Not "every IDomainEvent": the map is the set the outbox can actually carry.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
        {
            object sample = DomainEventSamples.Create(type);
            string json = JsonSerializer.Serialize(sample, type, options);
            object? read = JsonSerializer.Deserialize(json, type, options);

            // Through the payload, because a record compares its list members by reference.
            JsonSerializer.Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void All_five_domain_events_are_stageable()
    {
        // The loop above is vacuous on an empty map, and naming each makes an added event fail until sampled.
        using ServiceProvider provider = Registered();

        provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents.ShouldBe(
            [
                typeof(OrderPlacedDomainEvent),
                typeof(OrderStockConfirmedDomainEvent),
                typeof(OrderConfirmedDomainEvent),
                typeof(OrderShippedDomainEvent),
                typeof(OrderCancelledDomainEvent)
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("""{"Amount":19.99,"Currency":"EUR","Note":{"Amount":1,"Currency":"USD"}}""")]
    [InlineData("""{"Note":{"Amount":1,"Currency":"USD"},"Amount":19.99,"Currency":"EUR"}""")]
    [InlineData("""{"Amount":19.99,"Note":[{"Amount":1}],"Currency":"EUR"}""")]
    public void A_money_payload_ignores_members_a_later_version_added(string json)
    {
        // §9.2 makes an added member backward-compatible, so the converter must skip the whole unknown value.
        using ServiceProvider provider = Registered();

        JsonSerializer
            .Deserialize<Money>(json, provider.GetRequiredService<OutboxJson>().Options)
            .ShouldBe(Money.Of(19.99m, "EUR"));
    }

    [Fact]
    public void An_address_payload_keeps_an_absent_second_line_absent()
    {
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        Address address = Address.Of("1 Test Street", null, "Almaty", "050000", "KZ");

        JsonSerializer
            .Deserialize<Address>(JsonSerializer.Serialize(address, options), options)
            .ShouldBe(address);
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ordering"] = "Server=none;Database=Ordering;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none",
                // AddRedisConnections reads both eagerly; nothing here resolves a multiplexer.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddOrderingInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>§12.4's deliberate obstacle: a new domain event with no sample fails, not skipped.</summary>
    private static class DomainEventSamples
    {
        private static readonly OrderId Order = OrderId.New();
        private static readonly CustomerId Customer = new(Guid.CreateVersion7());
        private static readonly Money Total = Money.Of(19.99m, "EUR");

        private static readonly IReadOnlyList<OrderLineSnapshot> Lines =
            [new OrderLineSnapshot(ProductId.New(), 2, Money.Of(9.995m, "EUR"))];

        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(OrderPlacedDomainEvent)] =
                new OrderPlacedDomainEvent(Order, Customer, Total, Lines, Raised),
            [typeof(OrderStockConfirmedDomainEvent)] =
                new OrderStockConfirmedDomainEvent(Order, Total, Raised),
            [typeof(OrderConfirmedDomainEvent)] = new OrderConfirmedDomainEvent(
                Order,
                Customer,
                PaymentReference.Of("pay_123"),
                Address.Of("1 Test Street", "Flat 4", "Almaty", "050000", "KZ"),
                Total,
                Lines,
                Raised),
            [typeof(OrderShippedDomainEvent)] =
                new OrderShippedDomainEvent(Order, Customer, TrackingNumber.Of("TRK-1"), Raised),
            [typeof(OrderCancelledDomainEvent)] =
                new OrderCancelledDomainEvent(
                    Order,
                    Customer,
                    CancellationReason.CustomerRequest,
                    CancellationOrigin.User,
                    Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
