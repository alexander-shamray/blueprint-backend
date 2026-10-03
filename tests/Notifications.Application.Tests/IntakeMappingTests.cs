using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Intake;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>
/// Each of §3.2's seven handlers maps its contract to the command and nothing else: commands built by hand would
/// pass a handler that read the wrong member, of which three are Guids.
/// </summary>
public class IntakeMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingDispatcher _dispatcher = new();

    [Fact]
    public async Task OrderPlaced_owes_order_placed_with_its_total_and_names_the_customer()
    {
        OrderPlaced placed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 12345.60m,
            Currency = "KZT",
            Lines = []
        };

        await new OrderPlacedHandler(_dispatcher).HandleAsync(placed, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                placed.MessageId,
                TemplateKeys.OrderPlaced,
                new NotificationParameters
                {
                    OrderId = placed.OrderId,
                    OccurredAt = At,
                    Amount = 12345.60m,
                    Currency = "KZT"
                },
                new OrderFact(placed.CustomerId, Cancellation: null)));
    }

    [Fact]
    public async Task OrderConfirmed_owes_order_confirmed_with_its_total_and_names_the_customer()
    {
        OrderConfirmed confirmed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 99.5m,
            Currency = "GBP",
            Lines = []
        };

        await new OrderConfirmedHandler(_dispatcher).HandleAsync(confirmed, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                confirmed.MessageId,
                TemplateKeys.OrderConfirmed,
                new NotificationParameters
                {
                    OrderId = confirmed.OrderId,
                    OccurredAt = At,
                    Amount = 99.5m,
                    Currency = "GBP"
                },
                new OrderFact(confirmed.CustomerId, Cancellation: null)));
    }

    [Fact]
    public async Task OrderCancelled_owes_order_cancelled_and_carries_its_reason_and_origin_to_the_record()
    {
        OrderCancelled cancelled = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            Reason = CancelReasons.StockTimeout,
            Origin = CancelOrigins.Workflow
        };

        await new OrderCancelledHandler(_dispatcher).HandleAsync(cancelled, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                cancelled.MessageId,
                TemplateKeys.OrderCancelled,
                new NotificationParameters
                {
                    OrderId = cancelled.OrderId,
                    OccurredAt = At,
                    CancelReason = CancelReasons.StockTimeout
                },
                new OrderFact(
                    cancelled.CustomerId,
                    new OrderCancellation(CancelReasons.StockTimeout, CancelOrigins.Workflow, At))));
    }

    [Fact]
    public async Task PaymentDeclined_owes_payment_declined_and_never_reads_the_provider_s_reason()
    {
        PaymentDeclined declined = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            Reason = "do_not_honour_51"
        };

        await new PaymentDeclinedHandler(_dispatcher).HandleAsync(declined, TestContext.Current.CancellationToken);

        RecordNotificationCommand sent =
            _dispatcher.Sent.ShouldHaveSingleItem().ShouldBeOfType<RecordNotificationCommand>();
        sent.ShouldBe(
            new RecordNotificationCommand(
                declined.MessageId,
                TemplateKeys.PaymentDeclined,
                new NotificationParameters { OrderId = declined.OrderId, OccurredAt = At },
                Order: null));

        // ADR-049: a provider's code is no message for a customer, and nothing here may branch on it.
        ParametersFormat.Write(sent.Parameters).ShouldNotContain("do_not_honour_51");
    }

    [Fact]
    public async Task PaymentRefunded_owes_payment_refunded_with_its_amount()
    {
        PaymentRefunded refunded = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            Reference = "pay_ref_1",
            Amount = 10.125m,
            Currency = "KWD"
        };

        await new PaymentRefundedHandler(_dispatcher).HandleAsync(refunded, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                refunded.MessageId,
                TemplateKeys.PaymentRefunded,
                new NotificationParameters
                {
                    OrderId = refunded.OrderId,
                    OccurredAt = At,
                    Amount = 10.125m,
                    Currency = "KWD"
                },
                Order: null));
    }

    [Fact]
    public async Task ShipmentDispatched_owes_shipment_dispatched_with_its_tracking_number()
    {
        ShipmentDispatched dispatched = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            TrackingNumber = "TRK-1"
        };

        await new ShipmentDispatchedHandler(_dispatcher).HandleAsync(dispatched, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                dispatched.MessageId,
                TemplateKeys.ShipmentDispatched,
                new NotificationParameters { OrderId = dispatched.OrderId, OccurredAt = At, TrackingNumber = "TRK-1" },
                Order: null));
    }

    [Fact]
    public async Task ShipmentDelivered_owes_shipment_delivered_with_its_tracking_number()
    {
        ShipmentDelivered delivered = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            TrackingNumber = "TRK-2"
        };

        await new ShipmentDeliveredHandler(_dispatcher).HandleAsync(delivered, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(
            new RecordNotificationCommand(
                delivered.MessageId,
                TemplateKeys.ShipmentDelivered,
                new NotificationParameters { OrderId = delivered.OrderId, OccurredAt = At, TrackingNumber = "TRK-2" },
                Order: null));
    }

    [Fact]
    public void Every_key_has_exactly_one_handler()
    {
        // §3.2's seven, one each: a handler added without a key, or a key with none, fails here.
        Type[] handlers =
        [
            .. typeof(OrderPlacedHandler).Assembly
                .GetTypes()
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
        ];

        handlers.Length.ShouldBe(TemplateKeys.Placeholders.Count);
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
