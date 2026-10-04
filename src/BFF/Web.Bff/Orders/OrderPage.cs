namespace Web.Bff.Orders;

/// <summary>The list route's page bounds (§6.5, ADR-016), owned by the read rather than by the request.</summary>
/// <remarks>A row carries its lines, so the ceiling times <c>OrderLimits.MaxLines</c> bounds a page (§6.6).</remarks>
public static class OrderPage
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 50;

    /// <summary>Clamped, never refused, as §10.7 says of <c>limit</c>.</summary>
    public static int Clamp(int limit) => Math.Clamp(limit, 1, MaxLimit);
}
