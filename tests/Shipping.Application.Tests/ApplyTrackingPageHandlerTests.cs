using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

/// <summary>
/// One carrier page applied to one shipment. The handler is where the page's
/// order stops mattering: the aggregate already refuses a superseded arrival,
/// and applying by rank is what makes one page's inserts and its raised events
/// the same whichever order the carrier listed them in (spec, section 5).
/// </summary>
public class ApplyTrackingPageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_reversed_page_despatches_before_it_delivers()
    {
        // The simulator's SIM-REVERSED script, at the layer that meets it.
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        Result result = await Handle(repository, shipment.Id,
        [
            new CarrierEvent("e2", TrackingStatus.Delivered, Now.AddHours(2)),
            new CarrierEvent("e1", TrackingStatus.Collected, Now.AddHours(1))
        ]);

        result.IsSuccess.ShouldBeTrue();
        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)]);
    }

    [Fact]
    public async Task An_unrecognised_status_is_stored_and_moves_nothing()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        await Handle(repository, shipment.Id,
        [
            new CarrierEvent("e1", TrackingStatus.Unrecognised, Now),
            new CarrierEvent("e2", TrackingStatus.InTransit, Now.AddMinutes(1))
        ]);

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.Count.ShouldBe(2, "a carrier's fact is kept whether or not it moves the row");
        shipment.DomainEvents.ShouldBeEmpty();
        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30), "a shipment still moving is polled again");
    }

    [Fact]
    public async Task An_empty_page_is_an_answer_and_only_reschedules()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        await Handle(repository, shipment.Id, []);

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.ShouldBeEmpty();
        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_repeated_page_raises_the_events_once()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);
        CarrierEvent[] page = [new CarrierEvent("e1", TrackingStatus.Collected, Now.AddHours(1))];

        await Handle(repository, shipment.Id, page);
        shipment.ClearDomainEvents();
        await Handle(repository, shipment.Id, page);

        shipment.TrackingEvents.Count.ShouldBe(1);
        shipment.DomainEvents.ShouldBeEmpty("the key makes a repeated page free, and the outbox is not asked twice");
    }

    [Fact]
    public async Task A_shipment_that_is_gone_is_a_refusal_and_not_a_throw()
    {
        // No code path deletes a shipment, so a null here is the repository's
        // contract met by a hand or a migration rather than by the service. A
        // throw from a worker is a row retried for ever; a refusal rolls the
        // unit back and the claim lapses.
        Result result = await Handle(new FakeShipments(null), ShipmentId.New(), []);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ShipmentErrors.NotFound);
    }

    private static Shipment Booked()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.ClearDomainEvents();
        return shipment;
    }

    private static Task<Result> Handle(
        FakeShipments repository,
        ShipmentId id,
        IReadOnlyList<CarrierEvent> page) =>
        new ApplyTrackingPageHandler(repository, TimeProvider.System).HandleAsync(
            new ApplyTrackingPageCommand(id, page, Now.AddSeconds(30)),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// <c>IShipmentRepository</c>, whole: the two members this handler never
    /// calls throw rather than answering, so a handler that started reading by
    /// order would fail here rather than pass.
    /// </summary>
    /// <remarks>
    /// Nested deliberately: this assembly already holds a <c>FakeShipments</c>
    /// that records what was added, and this one answers one shipment — two
    /// doubles for two questions rather than one shared helper.
    /// </remarks>
    private sealed class FakeShipments(Shipment? shipment) : IShipmentRepository
    {
        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            Task.FromResult(shipment is not null && shipment.Id == id ? shipment : null);

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            throw new NotSupportedException("The tracking path reads by shipment id.");

        public void Add(Shipment added) => throw new NotSupportedException();
    }
}
