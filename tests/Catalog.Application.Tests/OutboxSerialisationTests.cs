using System.Text.Json;
using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>The <c>Local</c> lane's payload contract (§9.4), over <see cref="MessageTypeMap"/>'s set.</summary>
/// <remarks>From the real registration: a hand-built <see cref="OutboxJson"/> misses a lost converter.</remarks>
public class OutboxSerialisationTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);

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

            JsonSerializer
                .Deserialize(json, type, options)
                .ShouldBe(sample, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Theory]
    [InlineData("""{"Amount":19.99,"Currency":"EUR","Note":{"Amount":1,"Currency":"USD"}}""")]
    [InlineData("""{"Note":{"Amount":1,"Currency":"USD"},"Amount":19.99,"Currency":"EUR"}""")]
    [InlineData("""{"Amount":19.99,"Note":[{"Amount":1}],"Currency":"EUR"}""")]
    public void A_money_payload_ignores_members_a_later_version_added(string json)
    {
        // §9.2 lets a later version add a member, so its whole value is skipped: skipping one token would take a
        // nested Amount for this one, which bites only when the unknown member comes first, hence the orderings.
        using ServiceProvider provider = Registered();

        JsonSerializer
            .Deserialize<Money>(json, provider.GetRequiredService<OutboxJson>().Options)
            .ShouldBe(Money.Of(19.99m, "EUR"));
    }

    [Fact]
    public void There_is_a_stageable_domain_event_to_round_trip()
    {
        using ServiceProvider provider = Registered();

        provider
            .GetRequiredService<MessageTypeMap>()
            .StageableDomainEvents
            .ShouldContain(typeof(ProductPublishedDomainEvent));
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Catalog"] = "Server=none;Database=Catalog;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none",
                // AddRedisConnections throws without both keys; nothing here resolves a multiplexer.
                ["ConnectionStrings:RedisCache"] = "redis.invalid:6379",
                ["ConnectionStrings:RedisCoordination"] = "redis.invalid:6380"
            })
            .Build();

        ServiceCollection services = new();
        services.AddCatalogInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>The obstacle <c>ContractSamples</c> is in §12.6: an event with no sample fails, not skips.</summary>
    private static class DomainEventSamples
    {
        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(ProductPublishedDomainEvent)] = new ProductPublishedDomainEvent(
                ProductId.New(),
                "Walnut desk",
                "https://cdn.example/desk.jpg",
                Money.Of(19.99m, "EUR"),
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
