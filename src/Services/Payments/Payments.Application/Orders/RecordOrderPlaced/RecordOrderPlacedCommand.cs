using Common.Application;

namespace Payments.Application.Orders.RecordOrderPlaced;

public sealed record RecordOrderPlacedCommand(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PlacedAt) : ICommand<Result>;
