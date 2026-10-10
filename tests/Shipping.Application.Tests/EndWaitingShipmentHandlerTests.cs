using Common.Application;
using Shipping.Application.Privacy.EndWaitingShipment;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class EndWaitingShipmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeShipments _shipments = new();

    private EndWaitingShipmentHandler Handler() => new(_shipments, new FixedClock(Now));

    [Fact]
    public async Task A_pending_shipment_ends_with_the_erased_reason_at_the_handlers_clock()
    {
        Shipment waiting = Held();

        Result result = await Handler().HandleAsync(
            new EndWaitingShipmentCommand(waiting.OrderId.Value),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        waiting.Status.ShouldBe(ShipmentStatus.Unfulfillable);
        waiting.UnfulfillableReason.ShouldBe(EndWaitingShipmentHandler.Reason);
        waiting.TerminalAt.ShouldBe(Now);
    }

    [Fact]
    public async Task A_booked_shipment_is_left_to_its_carrier()
    {
        Shipment booked = Held();
        booked.Book("car_1", "TRK1", Now);

        await Handler().HandleAsync(
            new EndWaitingShipmentCommand(booked.OrderId.Value),
            TestContext.Current.CancellationToken);

        booked.Status.ShouldBe(ShipmentStatus.Booked, "a parcel in transit needs no address");
    }

    [Fact]
    public async Task Ending_a_shipment_twice_changes_nothing_the_second_time()
    {
        Shipment waiting = Held();
        EndWaitingShipmentCommand command = new(waiting.OrderId.Value);
        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        EndWaitingShipmentHandler later = new(_shipments, new FixedClock(Now.AddHours(1)));
        Result again = await later.HandleAsync(command, TestContext.Current.CancellationToken);

        again.IsSuccess.ShouldBeTrue();
        waiting.TerminalAt.ShouldBe(Now, "a second pass at a later instant must not re-stamp the end");
        waiting.UnfulfillableReason.ShouldBe(EndWaitingShipmentHandler.Reason);
    }

    [Fact]
    public async Task An_order_with_no_shipment_is_a_success_and_creates_none()
    {
        Result result = await Handler().HandleAsync(
            new EndWaitingShipmentCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _shipments.Added.ShouldBeEmpty();
    }

    private Shipment Held()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now.AddDays(-1));
        _shipments.Hold(shipment);

        return shipment;
    }

    private sealed class FakeShipments : IShipmentRepository
    {
        private readonly Dictionary<Guid, Shipment> _byOrder = [];

        public List<Shipment> Added { get; } = [];

        public void Hold(Shipment shipment) => _byOrder[shipment.OrderId.Value] = shipment;

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            Task.FromResult(_byOrder.GetValueOrDefault(orderId.Value));

        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised.");

        public void Add(Shipment shipment) => Added.Add(shipment);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
