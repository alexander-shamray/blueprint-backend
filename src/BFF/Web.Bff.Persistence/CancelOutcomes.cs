namespace Web.Bff.Persistence;

/// <summary>§10.7's three members an <c>OrderCancelled</c> decides between, held by a check constraint.</summary>
public static class CancelOutcomes
{
    public const string Cancelled = "cancelled";

    public const string OutOfStock = "out_of_stock";

    public const string Declined = "declined";
}
