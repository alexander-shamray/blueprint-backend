using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders;

/// <summary>
/// The catalogue. Every <see cref="Error"/> the service can return is
/// constructed here and nowhere else — which is what makes <c>Code</c> a
/// bounded set rather than whatever string the nearest handler happened to
/// type.
/// </summary>
/// <remarks>
/// <c>Code</c> is a metric dimension (§9.8 tags <c>command.domain_rejected</c>
/// with it), so it has to be closed: a tag whose value set is unbounded is a
/// cardinality incident waiting for the first handler that interpolates an id
/// into a code. Note what is not in any code below — no order id, no customer
/// id, no count. Those belong in the description, which is written for a
/// person and never tagged onto an instrument.
/// </remarks>
public static class OrderErrors
{
    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    /// <summary>
    /// Returned for a <c>Shipped</c> order <b>and for a <c>Delivered</c>
    /// one</b>, which is why the description no longer names the first
    /// (<a href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/109">#109</a>).
    /// </summary>
    /// <remarks>
    /// <b>The text broadened and the code did not, deliberately.</b>
    /// <c>Order.Cancel</c> refuses both statuses and its own message
    /// interpolates the real one — so the accurate sentence was thrown and
    /// this inaccurate one served, because <c>CancelOrderHandler</c> catches
    /// the <c>DomainException</c> and discards its text. Splitting this into
    /// two errors was the other option and was refused: <c>Code</c> is a
    /// contract, <c>order.already_shipped</c> is a dimension value on §9.8's
    /// dashboard, and a second code would silently halve every series built
    /// on it.
    /// </remarks>
    public static readonly Error AlreadyShipped =
        Error.Rule(
            "order.already_shipped",
            "An order that has already shipped cannot be cancelled; raise a return instead.");

    /// <summary>
    /// The one <see cref="ErrorType.Unavailable"/> in this catalogue that is
    /// about this service's own state rather than a dependency's, and it
    /// earns the type on §9.8's definition rather than by analogy: a fault
    /// that time might fix. Ordering learns that stock was reserved and that
    /// payment was authorised on two different receive endpoints with no
    /// ordering between them, so <c>ConfirmOrder</c> can arrive first.
    /// Returning a <c>Rule</c> error would ack a paid order's confirmation for
    /// good.
    /// </summary>
    public static readonly Error StockNotConfirmed =
        Error.Unavailable("order.stock_not_confirmed", "Stock reservation has not been recorded yet.");

    public static readonly Error NotAwaitingPayment =
        Error.Rule("order.not_awaiting_payment", "The order is not awaiting payment.");

    /// <summary>
    /// <see cref="StockNotConfirmed"/> one state later: a despatch cannot
    /// precede the confirmation that allowed it, so an unconfirmed order here
    /// means the confirming command is still in flight.
    /// </summary>
    public static readonly Error NotConfirmed =
        Error.Unavailable("order.not_confirmed", "The order has not been confirmed yet.");

    public static readonly Error NotShippable =
        Error.Rule("order.not_shippable", "The order is not in a state that can be shipped.");

    public static readonly Error NotAwaitingStock =
        Error.Rule("order.not_awaiting_stock", "The order is not awaiting stock.");

    // 422 for the same reason as ProductsUnavailable: the request was
    // well-formed, and what it asks for is beyond what OrderAmounts.Ceiling
    // says this service can record.
    public static readonly Error TotalBeyondCeiling =
        Error.Rule("order.total_beyond_ceiling", "The order's total is beyond what this service can record.");

    // 422, not 400: the request was well-formed and the validator passed it.
    // The products are unpriceable, which is a fact about this service's state
    // and not something the caller phrased wrongly.
    public static Error ProductsUnavailable(IReadOnlyList<ProductId> missing) =>
        Error.Rule("order.products_unavailable", $"No price for {missing.Count} product(s).");
}
