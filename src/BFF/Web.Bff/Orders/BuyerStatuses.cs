using Web.Bff.Persistence;

namespace Web.Bff.Orders;

/// <summary>§10.7's closed status vocabulary, as the wire spells it.</summary>
/// <remarks>The three cancellation members are <see cref="CancelOutcomes"/>', which the schema's check holds.</remarks>
public static class BuyerStatuses
{
    public const string Placed = "placed";
    public const string Confirmed = "confirmed";
    public const string Dispatched = "dispatched";
    public const string Delivered = "delivered";
    public const string Cancelled = CancelOutcomes.Cancelled;
    public const string OutOfStock = CancelOutcomes.OutOfStock;
    public const string Declined = CancelOutcomes.Declined;
}
