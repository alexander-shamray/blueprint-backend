using Common.Contracts.Ordering.V1;

namespace Web.Bff.Orders;

/// <summary>E.6.10's map from an <c>OrderCancelled</c> to the member a buyer is shown, decided once at write.</summary>
public static class CancellationOutcome
{
    public static string Of(string? origin, string? reason) =>
        origin switch
        {
            CancelOrigins.Workflow => reason switch
            {
                CancelReasons.OutOfStock or CancelReasons.StockTimeout => BuyerStatuses.OutOfStock,
                CancelReasons.PaymentDeclined or CancelReasons.PaymentTimeout => BuyerStatuses.Declined,

                // customer_request, and any code outside CancelReasons: the member that claims least (E.6.10).
                _ => BuyerStatuses.Cancelled
            },

            // user, an absent origin and one outside CancelOrigins alike (E.6.10).
            _ => BuyerStatuses.Cancelled
        };
}
