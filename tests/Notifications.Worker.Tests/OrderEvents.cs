using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Notifications.Worker.Tests;

/// <summary>§3.2's seven events for one order, each with a fresh message id, as their publishers write them.</summary>
internal static class OrderEvents
{
    public static OrderPlaced Placed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = 12345.60m,
        Currency = "KZT",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 12345.60m)]
    };

    public static OrderConfirmed Confirmed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = 12345.60m,
        Currency = "KZT",
        Lines = [new ConfirmedLine(Guid.CreateVersion7(), 1, 12345.60m)]
    };

    public static OrderCancelled Cancelled(
        Guid order,
        Guid customer,
        DateTimeOffset at,
        string reason,
        string? origin) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            CustomerId = customer,
            Reason = reason,
            Origin = origin
        };

    public static PaymentDeclined Declined(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reason = "do_not_honour_51"
    };

    public static PaymentRefunded Refunded(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = 12345.60m,
        Currency = "KZT"
    };

    public static ShipmentDispatched Dispatched(Guid order, DateTimeOffset at, string trackingNumber = "ZZ-0042") =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            TrackingNumber = trackingNumber
        };

    public static ShipmentDelivered Delivered(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        TrackingNumber = "ZZ-0042"
    };
}
