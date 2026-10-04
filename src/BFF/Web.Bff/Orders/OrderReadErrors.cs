using Common.Application;

namespace Web.Bff.Orders;

/// <summary>Every <see cref="Error"/> the order read returns, constructed here and nowhere else (§10.5).</summary>
public static class OrderReadErrors
{
    /// <summary>One answer for an unknown, another buyer's or an unowned order, confirming none (§10.7).</summary>
    public static readonly Error NotFound = Error.NotFound("order.not_found", "No order with that id.");
}
