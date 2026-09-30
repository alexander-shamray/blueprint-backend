using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

/// <summary>ADR-054's tracking age applied to one leased shipment, at the handler's clock.</summary>
public class AbandonShipmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Handled = Now.AddDays(100);

    [Fact]
    public async Task A_despatched_shipment_is_abandoned_at_the_handlers_clock_and_left_unscheduled()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.Record("e1", TrackingStatus.Collected, Now, Now);
        shipment.ClearDomainEvents();

        Result result = await Handle(new FakeShipments(shipment), shipment.Id);

        result.IsSuccess.ShouldBeTrue();
        shipment.Status.ShouldBe(ShipmentStatus.Abandoned);
        shipment.TerminalAt.ShouldBe(Handled);
        shipment.NextPollAt.ShouldBeNull();
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shipment_that_is_gone_is_a_refusal_and_not_a_throw()
    {
        Result result = await Handle(new FakeShipments(null), ShipmentId.New());

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ShipmentErrors.NotFound);
    }

    private static Task<Result> Handle(FakeShipments repository, ShipmentId id) =>
        new AbandonShipmentHandler(repository, new FixedClock(Handled)).HandleAsync(
            new AbandonShipmentCommand(id),
            TestContext.Current.CancellationToken);

    /// <summary><c>IShipmentRepository</c>, whole, answering one shipment by id; its other members throw.</summary>
    private sealed class FakeShipments(Shipment? shipment) : IShipmentRepository
    {
        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            Task.FromResult(shipment is not null && shipment.Id == id ? shipment : null);

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            throw new NotSupportedException("The tracking path reads by shipment id.");

        public void Add(Shipment added) => throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
