using Common.Contracts.Catalog.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Web.Bff.Tests;

/// <summary>ADR-051's eight events, each with a fresh message id, as their publishers write them.</summary>
internal static class OrderEvents
{
    public const decimal Total = 59.97m;

    public const string Currency = "GBP";

    public const string TrackingNumber = "SIM-4F2A9C";

    public static readonly Guid Lamp = Guid.Parse("0192f1b0-0000-7000-8000-00000000a1a1");

    public static OrderPlaced Placed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = Total,
        Currency = Currency,
        Lines = [new PlacedLine(Lamp, 3, 19.99m)]
    };

    public static OrderConfirmed Confirmed(
        Guid order,
        Guid customer,
        DateTimeOffset at,
        IReadOnlyList<ConfirmedLine>? lines = null) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            CustomerId = customer,
            TotalAmount = Total,
            Currency = Currency,
            Lines = lines ?? [new ConfirmedLine(Lamp, 3, 19.99m)]
        };

    public static OrderCancelled Cancelled(
        Guid order,
        Guid customer,
        DateTimeOffset at,
        string reason = CancelReasons.CustomerRequest,
        string? origin = CancelOrigins.User) =>
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

    public static PaymentAuthorised Authorised(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = Total,
        Currency = Currency
    };

    public static PaymentRefunded Refunded(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = Total,
        Currency = Currency
    };

    public static ShipmentDispatched Dispatched(
        Guid order,
        DateTimeOffset at,
        string trackingNumber = TrackingNumber) =>
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
        TrackingNumber = TrackingNumber
    };

    public static ProductPublished Published(Guid product, string name, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = product,
        OccurredAt = at,
        ProductId = product,
        Name = name,
        ThumbnailUrl = null,
        Amount = 19.99m,
        Currency = Currency
    };
}
