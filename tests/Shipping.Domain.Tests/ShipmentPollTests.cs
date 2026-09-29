using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>
/// The one piece of the tracking worker's bookkeeping that belongs on the row:
/// when the next poll is due, and that a terminal shipment is never polled
/// again (spec, section 4).
/// </summary>
public class ShipmentPollTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Booked()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        return shipment;
    }

    [Fact]
    public void An_applied_page_schedules_the_next_poll()
    {
        Shipment shipment = Booked();

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public void A_delivered_shipment_is_never_polled_again()
    {
        Shipment shipment = Booked();
        shipment.Record("e1", TrackingStatus.Delivered, Now, Now);

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBeNull(
            "a terminal shipment has no further fact to learn, and a row still due is a row the claim keeps");
    }

    [Fact]
    public void A_page_applied_to_a_voided_shipment_schedules_no_poll()
    {
        // A property of PollApplied, whoever calls it: a carrier's void commits
        // through Shipment.ReleaseClaim and the tracking claim selects only
        // Booked and Dispatched rows, so no pass is expected to land here. A
        // terminal row is still never rescheduled if one does.
        Shipment shipment = Booked();
        shipment.Cancel(Now);
        shipment.CarrierCancelled(Now);

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBeNull();
    }
}
