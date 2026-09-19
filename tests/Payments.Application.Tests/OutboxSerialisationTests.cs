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

/// <summary>
/// The <c>Local</c> lane's payload contract (§9.4), here rather than in
/// §12.6's contract suite because the set it iterates comes from the
/// <see cref="MessageTypeMap"/>, not the contracts namespace it selects on.
/// </summary>
/// <remarks>
/// Both halves come out of the real <c>AddPaymentsInfrastructure</c>: a
/// hand-built <see cref="OutboxJson"/> would stay green if a converter's
/// registration were deleted, and registration is what can go missing.
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
            // re-serialising catches a member dropped on write and on read
            // alike, which an object comparison over a record's generated
            // equality would not distinguish from a correct round trip.
            JsonSerializer.Serialize(read, type, options)
                .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
        }
    }

    [Fact]
    public void All_three_domain_events_are_stageable()
    {
        // The loop above is vacuous if the map is empty, and it would be
        // vacuous quietly — a registration that stopped naming Payments.Domain
        // would turn the assertion into a no-op and nothing else would say so.
        // Naming each rather than counting them also makes an added event a
        // decision: it fails here until it has a sample.
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

    /// <summary>
    /// A deliberate obstacle, the same one <c>ContractSamples</c> is in §12.6:
    /// a new domain event with no sample fails here instead of being skipped,
    /// which is the failure mode of every loop over types that falls back to
    /// <c>Activator.CreateInstance</c> — a parameterless record would produce
    /// an all-default instance that round-trips perfectly and proves nothing.
    /// </summary>
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
