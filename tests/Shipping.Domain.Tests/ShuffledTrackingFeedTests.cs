using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>
/// Spec section 5: the key `(ShipmentId, CarrierEventId)` orders nothing, so
/// the state machine is monotonic by rank rather than by arrival. A carrier
/// page can hold `delivered` above `collected` — the simulator's
/// `SIM-REVERSED` is exactly that — and the timeline the platform publishes
/// must not depend on which order the feed arrived in.
/// </summary>
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
            shipment.TrackingEvents.Select(e => e.CarrierEventId)
                .ShouldBe(["e1", "e2", "e3", "e4"], ignoreOrder: true, customMessage: arrival);

            // Despatch before delivery, whatever order the carrier reported
            // them in. Ordering's saga finalises on the first and Notifications
            // reads both, so a delivery ahead of a despatch is a timeline no
            // consumer can make sense of.
            shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
                [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)], arrival);
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
