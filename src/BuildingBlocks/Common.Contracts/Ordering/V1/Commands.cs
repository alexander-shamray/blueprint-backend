namespace Common.Contracts.Ordering.V1;

/// <summary>
/// Cancel an order (§3.2's Accepts column). Sent by the fulfilment saga, never
/// published: a command has exactly one owner (§9.6).
/// </summary>
/// <remarks>
/// A command implements no <see cref="IIntegrationEvent"/> and carries no
/// envelope; <c>CommandConsumer</c> routes it (§9.1, §9.4). <c>Reason</c> is a
/// <see cref="CancelReasons"/> code, and Ordering's command mapper fails loudly
/// on an unknown one, before any handler runs, rather than defaulting.
/// </remarks>
public sealed record CancelOrder(Guid OrderId, string Reason);

/// <summary>
/// Confirm an order once payment is authorised (§9.6).
/// </summary>
/// <remarks>
/// <c>PaymentReference</c> is a string: the reference originates in Payments as
/// an opaque provider token, and Ordering's <c>PaymentReference</c> value object
/// is a domain type a contract may not name (§9.1).
/// </remarks>
public sealed record ConfirmOrder(Guid OrderId, string PaymentReference);

/// <summary>
/// Record that an order has been despatched (§9.6).
/// </summary>
/// <remarks>
/// Despatch is Shipping's fact; recording it on the order is Ordering's
/// decision, so the saga sends this command rather than Ordering subscribing to
/// <c>ShipmentDispatched</c> directly. The aggregate still enforces the
/// transition.
/// </remarks>
public sealed record MarkOrderShipped(Guid OrderId, string TrackingNumber);

/// <summary>
/// Escalate an order to a human (§9.6), for anything the workflow cannot
/// resolve itself.
/// </summary>
/// <remarks>
/// It does not touch the <c>Order</c> aggregate: a human needing to look is a
/// fact about operations, and the row lands in an operations table whatever
/// the order's state. <see cref="ReviewReasons"/> says what each code means,
/// and which of them leave authorised money behind.
/// </remarks>
public sealed record FlagOrderForReview(Guid OrderId, string Reason);

/// <summary>
/// The wire vocabulary for <see cref="CancelOrder.Reason"/> and
/// <see cref="OrderCancelled.Reason"/>. One mapping back to
/// <c>CancellationReason</c>, in one place.
/// </summary>
/// <remarks>
/// A <c>static class</c> compiles to <c>abstract sealed</c>, which is what keeps
/// this out of §12.6's contract suite without a special case: that filter asks
/// for concrete types, and a vocabulary is not one.
/// </remarks>
public static class CancelReasons
{
    public const string OutOfStock = "out_of_stock";

    public const string StockTimeout = "stock_timeout";

    public const string PaymentDeclined = "payment_declined";

    /// <summary>
    /// Deliberately distinct from <see cref="PaymentDeclined"/>. The two
    /// compensate identically and mean opposite things — the first is the
    /// customer's bank saying no, the second is the PSP saying nothing. They
    /// are one dimension value apart on <c>orders.cancelled</c> (§13.3) and a
    /// different incident.
    /// </summary>
    public const string PaymentTimeout = "payment_timeout";

    public const string CustomerRequest = "customer_request";
}

/// <summary>
/// The wire vocabulary for <see cref="OrderCancelled.Origin"/> — who asked for
/// a cancellation, which <see cref="CancelReasons"/> deliberately does not say.
/// </summary>
/// <remarks>
/// A partition, not a list: §9.6's saga asks only whether it caused the
/// cancellation, so a member per ingress would invite a switch that forgets
/// one.
/// </remarks>
public static class CancelOrigins
{
    /// <summary>
    /// §11.4's endpoint, with a principal behind it — the customer, or an
    /// operator holding <c>orders:admin</c>.
    /// </summary>
    /// <remarks>
    /// An absent <see cref="OrderCancelled.Origin"/> does not mean this; that
    /// property says what it does mean.
    /// </remarks>
    public const string User = "user";

    /// <summary>
    /// §9.6's fulfilment saga compensating — the <c>CancelOrder</c> one of its
    /// own branches sent, arriving with no principal at all.
    /// </summary>
    public const string Workflow = "workflow";
}

/// <summary>
/// The wire vocabulary for <see cref="FlagOrderForReview.Reason"/> (§9.6).
/// </summary>
public static class ReviewReasons
{
    public const string NotDespatched = "not_despatched";

    public const string StockNotReleased = "stock_not_released";

    /// <summary>
    /// An authorisation landed while the saga was already compensating.
    /// </summary>
    /// <remarks>
    /// Raised on a <c>PaymentAuthorised</c> in <c>Compensating</c>, which a
    /// cancellation, a decline or a timeout enters, so no <c>OrderCancelled</c>
    /// need exist yet; or in <c>AwaitingPayment</c> once a cancellation was
    /// observed (§9.6). No despatch is left to stop, and whether Payments
    /// voided the authorisation is not knowable here (§9.4).
    /// </remarks>
    public const string PaymentAuthorisedDuringCompensation = "payment_authorised_during_compensation";

    /// <summary>
    /// A cancellation met an order the aggregate had confirmed.
    /// </summary>
    /// <remarks>
    /// Raised once §9.6's saga knows the order was confirmed, from its
    /// <c>OrderConfirmed</c> or a despatch, and has observed a cancellation, in
    /// either order; §9.4 orders nothing between them. Distinct from
    /// <see cref="PaymentAuthorisedDuringCompensation"/> because a despatch may
    /// be live, and a review row keeps the order, the code and the time but no
    /// saga state.
    /// </remarks>
    public const string CancelledAfterConfirmation = "cancelled_after_confirmation";

    /// <summary>
    /// Payment was authorised and the order never acknowledged the
    /// <c>ConfirmOrder</c> that followed.
    /// </summary>
    /// <remarks>
    /// The bound on §9.6's <c>AwaitingConfirmation</c>, escalating because a
    /// card is charged and Ordering has no refund command (§3.2). A
    /// <c>ConfirmOrder</c> the aggregate rejects is a <c>Rule</c> failure
    /// instead (§9.8); Ordering's <c>order.not_confirmed</c> error is an
    /// unrelated code.
    /// </remarks>
    public const string NotConfirmed = "not_confirmed";
}
