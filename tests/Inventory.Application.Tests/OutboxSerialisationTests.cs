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

/// <summary>
/// The <c>Local</c> lane's payload contract (§9.4). No containers, and it
/// lives here rather than in §12.6's contract suite because the set it
/// iterates comes from the <see cref="MessageTypeMap"/> and that suite selects
/// on the contracts namespace, which no domain event is in.
/// </summary>
/// <remarks>
/// Both halves come out of the real <c>AddInventoryInfrastructure</c>, and
/// that is the whole design of this test. A hand-built <see cref="OutboxJson"/>
/// asserting the one converter this service needs would stay green if the
/// registration that wires it into the running host were deleted. Registration
/// is the thing that can silently go missing, so registration is what this
/// resolves.
/// </remarks>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
    {
        // Not "every IDomainEvent": the map is the set the outbox can actually
        // carry, and a type it does not know cannot reach a payload column.
        using ServiceProvider provider = Registered();
        JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

        foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
        {
            object sample = DomainEventSamples.Create(type);
            string json = JsonSerializer.Serialize(sample, type, options);
            object? read = JsonSerializer.Deserialize(json, type, options);

            // Compared through the payload rather than with ShouldBe(sample):
            // re-serialising is what catches a member dropped on write and on
            // read together, the same reason Ordering's version of this test
            // does not compare the object directly either.
            JsonSerializer.Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void The_stageable_set_is_exactly_the_events_this_service_raises()
    {
        // The loop above is vacuous if the map is empty, and it would be
        // vacuous quietly — a registration that stopped naming
        // Inventory.Domain would turn the assertion into a no-op and nothing
        // else would say so. This service has two aggregates and four domain
        // events today, so the set is exactly those four; a fifth event added
        // without a sample fails here rather than being skipped.
        using ServiceProvider provider = Registered();

        provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents.ShouldBe(
            [
                typeof(StockLevelChangedDomainEvent),
                typeof(StockReservedDomainEvent),
                typeof(StockReservationFailedDomainEvent),
                typeof(StockReleasedDomainEvent)
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
                // AddRedisConnections reads both eagerly and throws naming the
                // missing one, so the two lines below are what let
                // AddInventoryInfrastructure run at all — the same reason the
                // bus key above is here. Nothing resolves a multiplexer in
                // this suite: the keyed registrations are factories, and no
                // test asks for one.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddInventoryApplication();
        services.AddInventoryInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A deliberate obstacle, the same one <c>ContractSamples</c> is in §12.6:
    /// a new domain event with no sample fails here instead of being skipped,
    /// which is the failure mode of every loop over types that falls back to
    /// <c>Activator.CreateInstance</c> — a parameterless record would produce
    /// an all-default instance that round-trips perfectly and proves nothing.
    /// </summary>
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
                new StockReleasedDomainEvent(OrderId.New(), Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
