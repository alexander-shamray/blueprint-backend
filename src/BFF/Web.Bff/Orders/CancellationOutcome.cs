using Common.Contracts.Ordering.V1;

namespace Web.Bff.Orders;

/// <summary>§10.7's map from an <c>OrderCancelled</c> to the member a buyer is shown, decided once at write.</summary>
public static class CancellationOutcome
{
    public static string Of(string? origin, string? reason) =>
        origin switch
        {
            CancelOrigins.Workflow => reason switch
            {
                CancelReasons.OutOfStock or CancelReasons.StockTimeout => BuyerStatuses.OutOfStock,
                CancelReasons.PaymentDeclined or CancelReasons.PaymentTimeout => BuyerStatuses.Declined,

                // customer_request, and any code outside CancelReasons: the member that claims least (§10.7).
                _ => BuyerStatuses.Cancelled
            },

            // user, an absent origin and one outside CancelOrigins alike (§10.7).
            _ => BuyerStatuses.Cancelled
        };
}
