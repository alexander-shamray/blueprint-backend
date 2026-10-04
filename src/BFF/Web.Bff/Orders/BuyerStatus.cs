namespace Web.Bff.Orders;

/// <summary>§10.7's rank: the highest step a row has absorbed, which needs no clock and so no ordering.</summary>
public static class BuyerStatus
{
    /// <summary>Null only for a row no step has reached, which no owned row is.</summary>
    public static string? Of(OrderSteps steps)
    {
        // Above a cancellation: §10.7 makes it highest, and goods that reached the buyer did reach them.
        if (steps.DeliveredAt is not null)
            return BuyerStatuses.Delivered;

        if (steps.CancelledAt is not null)
            return steps.CancelOutcome ?? BuyerStatuses.Cancelled;

        if (steps.DispatchedAt is not null)
            return BuyerStatuses.Dispatched;

        if (steps.ConfirmedAt is not null)
            return BuyerStatuses.Confirmed;

        return steps.PlacedAt is not null ? BuyerStatuses.Placed : null;
    }
}
