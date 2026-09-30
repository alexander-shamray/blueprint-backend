namespace Common.Contracts.Ordering.V1;

/// <summary>Sent by the saga, never published; <c>Reason</c> is a <see cref="CancelReasons"/> code (§9.6).</summary>
/// <remarks>A command carries no envelope and is deduplicated on the transport's id (§9.1, §9.5).</remarks>
public sealed record CancelOrder(Guid OrderId, string Reason);

public sealed record ConfirmOrder(Guid OrderId, string PaymentReference);

/// <summary>Sent by the saga: despatch is Shipping's fact, the order's record Ordering's (§9.6).</summary>
public sealed record MarkOrderShipped(Guid OrderId, string TrackingNumber);

/// <summary>Escalates to a human without touching the <c>Order</c> aggregate (§9.6).</summary>
public sealed record FlagOrderForReview(Guid OrderId, string Reason);

/// <summary>The wire codes for <see cref="CancelOrder.Reason"/> and <see cref="OrderCancelled.Reason"/>.</summary>
/// <remarks>Static, so §12.6's contract suite, which asks for concrete types, never reaches it.</remarks>
public static class CancelReasons
{
    public const string OutOfStock = "out_of_stock";

    public const string StockTimeout = "stock_timeout";

    public const string PaymentDeclined = "payment_declined";

    /// <summary>Compensates as a decline does, yet is a different incident (§13.3).</summary>
    public const string PaymentTimeout = "payment_timeout";

    public const string CustomerRequest = "customer_request";
}

/// <summary>Who asked for a cancellation, as a partition: §9.6's saga asks only whether it caused one.</summary>
public static class CancelOrigins
{
    /// <summary>§11.4's endpoint, with a principal behind it.</summary>
    public const string User = "user";

    /// <summary>§9.6's saga compensating, with no principal.</summary>
    public const string Workflow = "workflow";
}

/// <summary>The wire codes for <see cref="FlagOrderForReview.Reason"/> (§9.6).</summary>
public static class ReviewReasons
{
    public const string NotDespatched = "not_despatched";

    public const string StockNotReleased = "stock_not_released";

    /// <summary>An authorisation landed after the saga observed a cancellation or began compensating (§9.6).</summary>
    public const string PaymentAuthorisedDuringCompensation = "payment_authorised_during_compensation";

    /// <summary>A cancellation met a confirmed order, whose despatch may be live (§9.6).</summary>
    public const string CancelledAfterConfirmation = "cancelled_after_confirmation";

    /// <summary>A card is charged and the order never acknowledged <c>ConfirmOrder</c> (§9.6).</summary>
    public const string NotConfirmed = "not_confirmed";
}
