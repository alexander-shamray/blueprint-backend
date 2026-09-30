using Common.Application;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shipping.Application.Orders.RecordOrderCancelled;
using Shipping.Application.Orders.RecordOrderConfirmed;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

/// <summary>§3.2's Consumes column: each consumer writes one row, and the two writes commute.</summary>
public class ShipmentConsumerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeShipments _shipments = new();

    private CreateShipmentHandler Confirm() => new(_shipments, new FixedClock(Now));

    private VoidShipmentHandler Void(ILogger<VoidShipmentHandler>? log = null) =>
        new(_shipments, new FixedClock(Now.AddMinutes(1)), log ?? NullLogger<VoidShipmentHandler>.Instance);

    private static Guid Order() => Guid.CreateVersion7();

    [Fact]
    public async Task A_confirmed_order_becomes_a_pending_shipment()
    {
        Guid order = Order();

        await Confirm().HandleAsync(new CreateShipmentCommand(order), TestContext.Current.CancellationToken);

        Shipment shipment = _shipments.Added.ShouldHaveSingleItem();
        shipment.OrderId.ShouldBe(new OrderId(order));
        shipment.Status.ShouldBe(ShipmentStatus.Pending);
        shipment.NextAttemptAt.ShouldBe(Now, "the first pass may claim it at once");
    }

    [Fact]
    public async Task A_redelivered_confirmation_writes_no_second_shipment()
    {
        Guid order = Order();
        await Confirm().HandleAsync(new CreateShipmentCommand(order), TestContext.Current.CancellationToken);

        await Confirm().HandleAsync(new CreateShipmentCommand(order), TestContext.Current.CancellationToken);

        _shipments.Added.Count.ShouldBe(1, "§3.2 gives one shipment per confirmed order");
    }

    [Fact]
    public async Task A_cancellation_after_a_confirmation_voids_the_pending_shipment()
    {
        Guid order = Order();
        await Confirm().HandleAsync(new CreateShipmentCommand(order), TestContext.Current.CancellationToken);

        await Void().HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        _shipments.Added.ShouldHaveSingleItem().Status.ShouldBe(ShipmentStatus.Voided);
    }

    [Fact]
    public async Task A_cancellation_before_a_confirmation_writes_a_voided_tombstone()
    {
        Guid order = Order();

        await Void().HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        Shipment tombstone = _shipments.Added.ShouldHaveSingleItem();
        tombstone.OrderId.ShouldBe(new OrderId(order));
        tombstone.Status.ShouldBe(ShipmentStatus.Voided, "§9.4 orders nothing, so the late confirmation finds this");
    }

    [Fact]
    public async Task The_late_confirmation_finds_the_tombstone_and_does_nothing()
    {
        Guid order = Order();
        await Void().HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        await Confirm().HandleAsync(new CreateShipmentCommand(order), TestContext.Current.CancellationToken);

        _shipments.Added.Count.ShouldBe(1);
        _shipments.Added[0].Status.ShouldBe(
            ShipmentStatus.Voided,
            "the two writes commute: whichever arrives second reaches the same terminal state");
    }

    [Fact]
    public async Task A_second_cancellation_moves_nothing()
    {
        Guid order = Order();
        CapturingLogger<VoidShipmentHandler> log = new();
        await Void(log).HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        await Void(log).HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        Shipment tombstone = _shipments.Added.ShouldHaveSingleItem();
        tombstone.Status.ShouldBe(ShipmentStatus.Voided, "a superseded arrival returns rather than throwing");
        tombstone.CancellationRequestedAt.ShouldBeNull();
        AssertSupersededLogged(log, tombstone.Id, order);
    }

    [Fact]
    public async Task A_cancellation_of_a_delivered_shipment_moves_nothing_and_does_not_throw()
    {
        Guid order = Order();
        Shipment delivered = Shipment.For(ShipmentId.New(), new OrderId(order), Now);
        delivered.Book("crr_1", "TRK1", Now);
        delivered.Record("e1", TrackingStatus.Delivered, Now, Now);
        _shipments.Seed(delivered);
        CapturingLogger<VoidShipmentHandler> log = new();

        await Void(log).HandleAsync(new VoidShipmentCommand(order), TestContext.Current.CancellationToken);

        delivered.Status.ShouldBe(ShipmentStatus.Delivered);
        delivered.CancellationRequestedAt.ShouldBeNull();
        AssertSupersededLogged(log, delivered.Id, order);
    }

    // Pinned whole rather than sampled, so a parameter slipped into the template, an address among them, fails here.
    private static void AssertSupersededLogged(
        CapturingLogger<VoidShipmentHandler> log, ShipmentId shipment, Guid order)
    {
        (LogLevel Level, EventId EventId, string Message) entry = log.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe(
            $"Cancellation of shipment {shipment.Value} for order {order} moved nothing; already superseded.");
    }

    [Fact]
    public async Task Each_handler_maps_the_contract_and_nothing_else()
    {
        // The contract half, since every assertion above builds the command by hand and both ids are a Guid.
        OrderConfirmed confirmed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            OrderId = Order(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 42.10m,
            Currency = "EUR",
            Lines = []
        };

        RecordingDispatcher confirmedDispatcher = new();
        await new OrderConfirmedHandler(confirmedDispatcher)
            .HandleAsync(confirmed, TestContext.Current.CancellationToken);

        confirmedDispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new CreateShipmentCommand(confirmed.OrderId));

        OrderCancelled cancelled = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            OrderId = Order(),
            CustomerId = Guid.CreateVersion7(),
            Reason = CancelReasons.CustomerRequest,
            Origin = CancelOrigins.User
        };

        RecordingDispatcher cancelledDispatcher = new();
        await new OrderCancelledHandler(cancelledDispatcher)
            .HandleAsync(cancelled, TestContext.Current.CancellationToken);

        cancelledDispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new VoidShipmentCommand(cancelled.OrderId));
    }

    private sealed class FakeShipments : IShipmentRepository
    {
        private readonly Dictionary<OrderId, Shipment> _byOrder = [];
        private readonly Dictionary<ShipmentId, Shipment> _byId = [];

        public List<Shipment> Added { get; } = [];

        public void Seed(Shipment shipment)
        {
            _byOrder[shipment.OrderId] = shipment;
            _byId[shipment.Id] = shipment;
        }

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            Task.FromResult(_byOrder.GetValueOrDefault(orderId));

        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public void Add(Shipment shipment)
        {
            Added.Add(shipment);
            Seed(shipment);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The formatted message rather than the raw state, so an assertion can pin the template whole.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
