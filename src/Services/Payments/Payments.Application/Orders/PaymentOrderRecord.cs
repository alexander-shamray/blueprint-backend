using Payments.Domain.Orders;

namespace Payments.Application.Orders;

public sealed record PaymentOrderRecord(
    OrderId OrderId,
    Guid? CustomerId,
    decimal? TotalAmount,
    string? Currency,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? CancelledAt)
{
    public bool IsPlaced => PlacedAt is not null;
    public bool IsCancelled => CancelledAt is not null;
}
