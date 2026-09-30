using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class CarrierPortTests
{
    [Fact]
    public void The_keys_are_the_shipment_and_differ_between_the_two_acts()
    {
        ShipmentId shipment = ShipmentId.New();

        string book = new BookingRequest(shipment, Address()).IdempotencyKey;
        string cancel = new CancellationRequest(shipment, "crr_x").IdempotencyKey;

        book.ShouldBe($"book:{shipment.Value}");
        cancel.ShouldBe($"cancel:{shipment.Value}");
        cancel.ShouldNotBe(book, "a cancel replayed under the booking's key would return the booking");
    }

    [Fact]
    public void A_booking_is_booked_or_refused_and_nothing_else()
    {
        typeof(BookingResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(BookingResult)))
            .Select(t => t.Name)
            .ShouldBe(["Booked", "Refused"], ignoreOrder: true,
                "a transient fault is an exception, so an outage can never reach the row as a refusal");
    }

    [Fact]
    public void A_cancellation_is_cancelled_or_too_late_and_nothing_else()
    {
        typeof(CancellationResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(CancellationResult)))
            .Select(t => t.Name)
            .ShouldBe(["Cancelled", "TooLate"], ignoreOrder: true,
                "section 6: the carrier either voids the shipment or says the parcel has gone");
    }

    [Fact]
    public void A_carrier_event_carries_no_link_of_the_carriers()
    {
        // No URL is stored, checked on the shape: a field is the one way a link could be kept.
        typeof(CarrierEvent).GetProperties()
            .Select(p => p.Name)
            .ShouldBe(["CarrierEventId", "Status", "OccurredAt"], ignoreOrder: true);
    }

    private static DeliveryAddress Address() =>
        new("1 Abay Avenue", null, "Almaty", "050000", "KZ");
}
