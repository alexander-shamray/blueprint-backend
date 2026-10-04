namespace Web.Bff.Orders;

/// <summary>The five step instants of one <c>bff.Orders</c> row and the member its cancellation mapped to.</summary>
public readonly record struct OrderSteps(
    DateTimeOffset? PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    string? CancelOutcome);
