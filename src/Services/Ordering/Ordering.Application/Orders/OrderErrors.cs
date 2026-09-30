using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>Every <see cref="Error"/> the service returns is built here, so <c>Code</c> is a closed set.</summary>
/// <remarks><c>Code</c> is a metric dimension, so no id or count goes in one (§10.5).</remarks>
public static class OrderErrors
{
    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    /// <summary>For <c>Delivered</c> as well as <c>Shipped</c>; one code, being a §9.8 dimension.</summary>
    public static readonly Error AlreadyShipped =
        Error.Rule(
            "order.already_shipped",
            "An order that has already shipped cannot be cancelled; raise a return instead.");

    /// <summary>A fault time may fix: stock and payment arrive on separate endpoints, so this can come first.</summary>
    public static readonly Error StockNotConfirmed =
        Error.Unavailable("order.stock_not_confirmed", "Stock reservation has not been recorded yet.");

    public static readonly Error NotAwaitingPayment =
        Error.Rule("order.not_awaiting_payment", "The order is not awaiting payment.");

    /// <summary><see cref="StockNotConfirmed"/> one state later: the confirming command is still in flight.</summary>
    public static readonly Error NotConfirmed =
        Error.Unavailable("order.not_confirmed", "The order has not been confirmed yet.");

    public static readonly Error NotShippable =
        Error.Rule("order.not_shippable", "The order is not in a state that can be shipped.");

    public static readonly Error NotAwaitingStock =
        Error.Rule("order.not_awaiting_stock", "The order is not awaiting stock.");

    // 422 like ProductsUnavailable: well-formed, but beyond what OrderAmounts.Ceiling can record.
    public static readonly Error TotalBeyondCeiling =
        Error.Rule("order.total_beyond_ceiling", "The order's total is beyond what this service can record.");

    // 422, not 400: the request was well-formed and the validator passed it.
    // The products are unpriceable, which is a fact about this service's state
    // and not something the caller phrased wrongly.
    public static Error ProductsUnavailable(IReadOnlyList<ProductId> missing) =>
        Error.Rule("order.products_unavailable", $"No price for {missing.Count} product(s).");
}
