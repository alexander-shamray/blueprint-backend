using Common.Domain;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

public class ShipmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Pending() => Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);

    private static Shipment Booked()
    {
        Shipment shipment = Pending();
        shipment.Book("car_1", "TRK1", Now).ShouldBeTrue();
        return shipment;
    }

    [Fact]
    public void A_confirmed_order_creates_a_pending_shipment_and_raises_nothing()
    {
        Shipment shipment = Pending();

        shipment.Status.ShouldBe(ShipmentStatus.Pending);
        shipment.CarrierReference.ShouldBeNull();
        shipment.TrackingNumber.ShouldBeNull();
        shipment.TerminalAt.ShouldBeNull();
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void The_carrier_booking_records_its_reference_and_tracking_number()
    {
        Shipment shipment = Booked();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.CarrierReference.ShouldBe("car_1");
        shipment.TrackingNumber.ShouldBe("TRK1");
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void An_answer_that_it_cannot_be_done_is_terminal_and_carries_its_reason()
    {
        Shipment shipment = Pending();

        shipment.MarkUnfulfillable("address_not_serviceable", Now).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Unfulfillable);
        shipment.UnfulfillableReason.ShouldBe("address_not_serviceable");
        shipment.TerminalAt.ShouldBe(Now);
    }

    [Fact]
    public void A_cancellation_voids_a_pending_shipment_and_only_requests_it_of_a_booked_one()
    {
        Shipment pending = Pending();
        pending.Cancel(Now).ShouldBeTrue();
        pending.Status.ShouldBe(ShipmentStatus.Voided);
        pending.TerminalAt.ShouldBe(Now);

        Shipment booked = Booked();
        booked.Cancel(Now).ShouldBeTrue();
        booked.Status.ShouldBe(ShipmentStatus.Booked, "the parcel may already be moving");
        booked.CancellationRequestedAt.ShouldBe(Now);
        booked.TerminalAt.ShouldBeNull();
    }

    [Fact]
    public void The_carrier_cancelling_voids_a_requested_cancellation()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now);

        shipment.CarrierCancelled(Now.AddMinutes(1)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Voided);
        shipment.TerminalAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void The_carrier_answering_too_late_stamps_the_refusal_and_tracking_goes_on()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now);

        shipment.CarrierRefusedCancellation(Now.AddMinutes(1)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.CancellationRefusedAt.ShouldBe(Now.AddMinutes(1));

        shipment.Record("e1", TrackingStatus.Collected, Now.AddMinutes(2), Now.AddMinutes(3)).ShouldBeTrue();
        shipment.Status.ShouldBe(ShipmentStatus.Dispatched);
    }

    [Fact]
    public void A_collected_event_despatches_a_booked_shipment()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(2)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Dispatched);
        shipment.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new ShipmentDispatchedDomainEvent(shipment.Id, shipment.OrderId, "TRK1", Now.AddHours(1)));
    }

    [Fact]
    public void A_delivered_event_on_a_booked_shipment_raises_the_despatch_first()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Delivered, Now.AddHours(1), Now.AddHours(2)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
        shipment.TerminalAt.ShouldBe(Now.AddHours(2));
        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)],
            "the despatch was never raised, and a delivery that precedes it is not a timeline");
    }

    [Fact]
    public void A_delivered_event_on_a_dispatched_shipment_raises_the_delivery_alone()
    {
        Shipment shipment = Booked();
        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(1));

        shipment.Record("e2", TrackingStatus.Delivered, Now.AddHours(2), Now.AddHours(2)).ShouldBeTrue();

        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)]);
    }

    [Fact]
    public void An_in_transit_or_unrecognised_event_is_recorded_and_moves_nothing()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.InTransit, Now, Now).ShouldBeFalse();
        shipment.Record("e2", TrackingStatus.Unrecognised, Now, Now).ShouldBeFalse();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.Count.ShouldBe(2, "a carrier's fact is kept whether or not it moves the row");
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void A_repeated_page_is_free()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Collected, Now, Now).ShouldBeTrue();
        shipment.Record("e1", TrackingStatus.Collected, Now, Now.AddHours(1)).ShouldBeFalse();

        shipment.TrackingEvents.Count.ShouldBe(1);
        shipment.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void Every_superseded_arrival_is_a_no_op_rather_than_a_throw()
    {
        Shipment delivered = Booked();
        delivered.Record("e1", TrackingStatus.Delivered, Now, Now);

        // A Collected after a Delivered, a second cancellation, a cancellation
        // of a delivered shipment, a booking of something already booked: each
        // is a fact already superseded, and a throw here is a worker row
        // retried for ever or a consumer redelivery loop into `_error`.
        delivered.Record("e2", TrackingStatus.Collected, Now, Now).ShouldBeFalse();
        delivered.Cancel(Now).ShouldBeFalse();
        delivered.Book("car_2", "TRK2", Now).ShouldBeFalse();
        delivered.MarkUnfulfillable("too_late", Now).ShouldBeFalse();
        delivered.CarrierCancelled(Now).ShouldBeFalse();
        delivered.CarrierRefusedCancellation(Now).ShouldBeFalse();

        delivered.Status.ShouldBe(ShipmentStatus.Delivered);
        delivered.TrackingNumber.ShouldBe("TRK1");
        delivered.DomainEvents.Count.ShouldBe(2);

        Shipment voided = Pending();
        voided.Cancel(Now);
        voided.Cancel(Now).ShouldBeFalse();
        voided.Book("car_2", "TRK2", Now).ShouldBeFalse();
        voided.Record("e1", TrackingStatus.Collected, Now, Now).ShouldBeFalse();
        voided.Status.ShouldBe(ShipmentStatus.Voided);

        Shipment unfulfillable = Pending();
        unfulfillable.MarkUnfulfillable("address_not_serviceable", Now);
        unfulfillable.Book("car_2", "TRK2", Now).ShouldBeFalse();
        unfulfillable.Cancel(Now).ShouldBeFalse();
        unfulfillable.Status.ShouldBe(ShipmentStatus.Unfulfillable);
    }

    [Fact]
    public void A_carrier_answer_the_columns_cannot_hold_is_a_broken_invariant()
    {
        // Malformed input is §5.7's DomainException and not a no-op: an empty
        // reference or one past the column's width is the adapter having
        // failed to bound what the carrier sent, which is a defect.
        Should.Throw<DomainException>(() => Pending().Book(" ", "TRK1", Now));
        Should.Throw<DomainException>(() => Pending().Book("car_1", "", Now));
        Should.Throw<DomainException>(() => Pending().MarkUnfulfillable("", Now));
        Should.Throw<DomainException>(() =>
            Pending().Book(new string('x', ShipmentLimits.MaxCarrierReferenceLength + 1), "TRK1", Now));
        Should.Throw<DomainException>(() =>
            Booked().Record("", TrackingStatus.Collected, Now, Now));
    }

    [Fact]
    public void An_id_differing_only_by_trailing_spaces_is_the_same_event_as_the_table_keys_it()
    {
        // SQL Server compares keys ignoring trailing spaces under every
        // collation, so "ev1" and "ev1 " are one key to the table. The
        // aggregate keys the same way, and the second is a repeated page.
        Shipment shipment = Booked();

        shipment.Record("ev1", TrackingStatus.InTransit, Now, Now).ShouldBeFalse();
        shipment.Record("ev1 ", TrackingStatus.InTransit, Now, Now).ShouldBeFalse();

        shipment.TrackingEvents.ShouldHaveSingleItem().CarrierEventId.ShouldBe("ev1");

        // A padded id on its own is still an id: nothing about it is refused.
        Booked().Record("ev2 ", TrackingStatus.Collected, Now, Now).ShouldBeTrue();
    }
}
