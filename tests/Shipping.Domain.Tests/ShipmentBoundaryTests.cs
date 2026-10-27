using Common.Domain;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>The guards' wording and edges, the transitions' preconditions, and the materialised defaults.</summary>
public class ShipmentBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Pending() => Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);

    private static Shipment Booked()
    {
        Shipment shipment = Pending();
        shipment.Book("car_1", "TRK1", Now).ShouldBeTrue();
        return shipment;
    }

    private static Shipment Voided()
    {
        Shipment shipment = Pending();
        shipment.Cancel(Now).ShouldBeTrue();
        return shipment;
    }

    [Fact]
    public void A_shipment_created_without_erasures_holds_none()
    {
        Pending().ErasedUnder.ShouldBeEmpty();
    }

    [Fact]
    public void A_shipment_keeps_the_erasures_its_customer_was_under_once_each()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();

        Shipment shipment = Shipment.For(
            ShipmentId.New(),
            new OrderId(Guid.CreateVersion7()),
            Now,
            [first, second, first]);

        shipment.ErasedUnder.ShouldBe([first, second]);
    }

    [Fact]
    public void A_booking_without_its_reference_or_number_is_refused_in_the_words_of_what_is_missing()
    {
        DomainException reference = Should.Throw<DomainException>(() => Pending().Book("", "TRK1", Now));
        reference.Message.ShouldBe("A shipment needs the carrier's reference.");

        DomainException number = Should.Throw<DomainException>(() => Pending().Book("car_1", " ", Now));
        number.Message.ShouldBe("A shipment needs a tracking number.");
    }

    [Fact]
    public void An_unfulfillable_answer_without_a_reason_is_refused_in_the_words_of_what_is_missing()
    {
        DomainException exception = Should.Throw<DomainException>(() => Pending().MarkUnfulfillable("", Now));

        exception.Message.ShouldBe("A shipment needs a reason.");
    }

    [Fact]
    public void An_event_without_the_carriers_id_is_refused_in_the_words_of_what_is_missing()
    {
        DomainException exception = Should.Throw<DomainException>(() =>
            Booked().Record(" ", TrackingStatus.Collected, Now, Now));

        exception.Message.ShouldBe("A shipment needs the carrier's event id.");
    }

    [Fact]
    public void A_kept_booking_without_its_reference_or_number_is_refused_and_changes_nothing()
    {
        Shipment withoutReference = Voided();
        DomainException reference = Should.Throw<DomainException>(() =>
            withoutReference.KeepUnreturnedBooking("", "TRK1", Now));
        reference.Message.ShouldBe("A shipment needs the carrier's reference.");
        withoutReference.Status.ShouldBe(ShipmentStatus.Voided);

        Shipment withoutNumber = Voided();
        DomainException number = Should.Throw<DomainException>(() =>
            withoutNumber.KeepUnreturnedBooking("car_1", "", Now));
        number.Message.ShouldBe("A shipment needs a tracking number.");
        withoutNumber.Status.ShouldBe(ShipmentStatus.Voided);
    }

    [Fact]
    public void A_value_one_past_its_column_is_refused_naming_the_width_and_one_at_it_is_kept()
    {
        Shipment reference = Pending();
        reference.Book(
            new string('r', ShipmentLimits.MaxCarrierReferenceLength),
            new string('t', ShipmentLimits.MaxTrackingNumberLength),
            Now).ShouldBeTrue();
        reference.CarrierReference!.Length.ShouldBe(ShipmentLimits.MaxCarrierReferenceLength);
        reference.TrackingNumber!.Length.ShouldBe(ShipmentLimits.MaxTrackingNumberLength);

        Shipment unfulfillable = Pending();
        unfulfillable.MarkUnfulfillable(new string('u', ShipmentLimits.MaxUnfulfillableReasonLength), Now)
            .ShouldBeTrue();

        Shipment recorded = Booked();
        recorded.Record(
            new string('e', ShipmentLimits.MaxCarrierEventIdLength),
            TrackingStatus.InTransit,
            Now,
            Now).ShouldBeFalse();
        recorded.TrackingEvents.ShouldHaveSingleItem();

        DomainException exception = Should.Throw<DomainException>(() =>
            Pending().Book(new string('r', ShipmentLimits.MaxCarrierReferenceLength + 1), "TRK1", Now));
        exception.Message.ShouldBe(
            $"the carrier's reference is longer than {ShipmentLimits.MaxCarrierReferenceLength} characters; "
            + "the adapter bounds it.");
    }

    [Fact]
    public void A_cancellation_the_carrier_was_never_asked_for_is_not_taken_back_by_it()
    {
        Booked().CarrierCancelled(Now).ShouldBeFalse();
    }

    [Fact]
    public void A_cancellation_requested_of_a_shipment_since_delivered_is_not_taken_back_by_the_carrier()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now).ShouldBeTrue();
        shipment.Record("e1", TrackingStatus.Delivered, Now, Now).ShouldBeTrue();

        shipment.CarrierCancelled(Now.AddMinutes(1)).ShouldBeFalse();

        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
    }

    [Fact]
    public void A_refusal_needs_a_requested_cancellation_on_a_booked_shipment_and_comes_once()
    {
        Booked().CarrierRefusedCancellation(Now).ShouldBeFalse();

        Shipment delivered = Booked();
        delivered.Cancel(Now).ShouldBeTrue();
        delivered.Record("e1", TrackingStatus.Delivered, Now, Now).ShouldBeTrue();
        delivered.CarrierRefusedCancellation(Now.AddMinutes(1)).ShouldBeFalse();
        delivered.CancellationRefusedAt.ShouldBeNull();

        Shipment refused = Booked();
        refused.Cancel(Now).ShouldBeTrue();
        refused.CarrierRefusedCancellation(Now.AddMinutes(1)).ShouldBeTrue();
        refused.CarrierRefusedCancellation(Now.AddMinutes(2)).ShouldBeFalse();
        refused.CancellationRefusedAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Releasing_a_claim_drops_its_lease_and_resets_its_backoff()
    {
        Shipment shipment = Booked();
        SetPrivately(shipment, nameof(Shipment.LockedUntil), (DateTimeOffset?)Now.AddMinutes(5));
        SetPrivately(shipment, nameof(Shipment.Attempts), 3);

        shipment.ReleaseClaim();

        shipment.LockedUntil.ShouldBeNull();
        shipment.Attempts.ShouldBe(0);
    }

    [Fact]
    public void A_tracking_event_a_store_materialises_starts_with_an_empty_carrier_id()
    {
        var trackingEvent = (TrackingEvent)Activator.CreateInstance(typeof(TrackingEvent), nonPublic: true)!;

        trackingEvent.CarrierEventId.ShouldBe("");
    }

    private static void SetPrivately<T>(Shipment shipment, string property, T value) =>
        typeof(Shipment).GetProperty(property)!.SetValue(shipment, value);
}
