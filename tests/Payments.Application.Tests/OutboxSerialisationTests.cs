using System.Text.Json;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
using Payments.Domain.Refunds.Events;
using Payments.Infrastructure;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

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
            JsonSerializer
                .Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void All_three_domain_events_are_stageable()
    {
        // Named rather than counted, so an empty map fails and an added event is a decision.
        using ServiceProvider provider = Registered();

        provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents.ShouldBe(
            [
                typeof(PaymentAuthorisedDomainEvent),
                typeof(PaymentDeclinedDomainEvent),
                typeof(PaymentRefundedDomainEvent)
            ],
            ignoreOrder: true);
    }

    private static ServiceProvider Registered()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Payments"] = "Server=none;Database=Payments;",
                ["ConnectionStrings:RabbitMq"] = "amqp://none"
            })
            .Build();

        ServiceCollection services = new();
        services.AddPaymentsInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>The obstacle <c>ContractSamples</c> is in §12.6: an event with no sample fails, not skips.</summary>
    private static class DomainEventSamples
    {
        private static readonly OrderId Order = OrderId.New();

        private static readonly Dictionary<Type, object> Samples = new()
        {
            [typeof(PaymentAuthorisedDomainEvent)] =
                new PaymentAuthorisedDomainEvent(Order, "psp_1", 42.10m, "EUR", Raised),
            [typeof(PaymentDeclinedDomainEvent)] =
                new PaymentDeclinedDomainEvent(Order, "card_declined", Raised),
            [typeof(PaymentRefundedDomainEvent)] =
                new PaymentRefundedDomainEvent(Order, "psp_1", 42.10m, "EUR", Raised)
        };

        public static object Create(Type type) =>
            Samples.TryGetValue(type, out object? sample) ? sample
                : throw new InvalidOperationException(
                    $"{type.Name} is stageable on the Local lane but has no sample here. Add one — " +
                    "the round-trip assertion is what stops a member rename from deserialising to " +
                    "its default in production (§9.4).");
    }
}
