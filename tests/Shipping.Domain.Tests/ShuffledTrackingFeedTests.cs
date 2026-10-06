using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>The events a shipment raises, and their order, do not depend on the order of a carrier's page.</summary>
public class ShuffledTrackingFeedTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly (string Id, TrackingStatus Status, int Minute)[] Feed =
    [
        ("e1", TrackingStatus.Collected, 10),
        ("e2", TrackingStatus.InTransit, 20),
        ("e3", TrackingStatus.Unrecognised, 30),
        ("e4", TrackingStatus.Delivered, 40)
    ];

    [Fact]
    public void Every_permutation_reaches_one_terminal_state_and_one_sequence_of_events()
    {
        int permutations = 0;

        foreach ((string, TrackingStatus, int)[] order in Permutations(Feed))
        {
            permutations++;
            Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Raised);
            shipment.Book("car_1", "TRK1", Raised);

            foreach ((string id, TrackingStatus status, int minute) in order)
                shipment.Record(id, status, Raised.AddMinutes(minute), Raised.AddMinutes(minute));

            string arrival = string.Join(",", order.Select(e => e.Item1));

            shipment.Status.ShouldBe(ShipmentStatus.Delivered, arrival);
            shipment.TrackingEvents
                .Select(e => e.CarrierEventId)
                .ShouldBe(["e1", "e2", "e3", "e4"], ignoreOrder: true, customMessage: arrival);

            // Despatch before delivery whatever the arrival order, since Notifications consumes both (§3.2).
            shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
                [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)],
                arrival);
        }

        // Four events have twenty-four orders; a generator that yielded fewer
        // would leave the loop above passing over the orders it never tried.
        permutations.ShouldBe(24);
    }

    /// <summary>Every ordering of the feed, by recursive selection.</summary>
    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int index = 0; index < items.Length; index++)
        {
            T[] rest = [.. items[..index], .. items[(index + 1)..]];

            foreach (T[] tail in Permutations(rest))
                yield return [items[index], .. tail];
        }
    }
}
