namespace Ordering.Domain.Orders;

/// <summary>Why an order was cancelled; closed, since §13.3 tags a metric with it.</summary>
/// <remarks>A member added here needs a code in <c>CancellationReasons</c>, which refuses unknowns (§11.4).</remarks>
public enum CancellationReason
{
    OutOfStock,
    StockTimeout,
    PaymentDeclined,
    PaymentTimeout,
    CustomerRequest
}
