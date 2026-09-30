using System.Text.Json;
using Common.Infrastructure.Outbox;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Inventory.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>The <c>Local</c> lane's payload contract (§9.4), over <see cref="MessageTypeMap"/>'s set.</summary>
/// <remarks>From the real registration: a hand-built <see cref="OutboxJson"/> misses a lost converter.</remarks>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
    {
        // The map's set, not every IDomainEvent: a type it does not know cannot reach a payload column.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
        {
            object sample = DomainEventSamples.Create(type);
            string json = JsonSerializer.Serialize(sample, type, options);
            object? read = JsonSerializer.Deserialize(json, type, options);

            // Compared by re-serialising, which catches a member dropped on write or on read alike.
            JsonSerializer.Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void The_stageable_set_is_exactly_the_events_this_service_raises()
    {
        // Named rather than counted, so an empty map fails and an added event is a decision.
        using ServiceProvider provider = Registered();

        provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents.ShouldBe(
            [
                typeof(StockLevelChangedDomainEvent),
                typeof(StockReservedDomainEvent),
                typeof(StockReservationFailedDomainEvent),
                typeof(StockReleasedDomainEvent),
                typeof(DespatchedUnreservedDomainEvent)
            ],
            ignoreOrder: true);
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Inventory"] = "Server=none;Database=Inventory;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none",
                // AddRedisConnections throws without both keys; nothing here resolves a multiplexer.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddInventoryApplication();
        services.AddInventoryInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>The obstacle <c>ContractSamples</c> is in §12.6: an event with no sample fails, not skips.</summary>
    private static class DomainEventSamples
    {
        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(StockLevelChangedDomainEvent)] =
                new StockLevelChangedDomainEvent(ProductId.New(), 7, Raised),
            [typeof(StockReservedDomainEvent)] =
                new StockReservedDomainEvent(OrderId.New(), Raised),
            [typeof(StockReservationFailedDomainEvent)] =
                new StockReservationFailedDomainEvent(OrderId.New(), [ProductId.New()], Raised),
            [typeof(StockReleasedDomainEvent)] =
                new StockReleasedDomainEvent(OrderId.New(), Raised),
            [typeof(DespatchedUnreservedDomainEvent)] =
                new DespatchedUnreservedDomainEvent(OrderId.New(), Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
