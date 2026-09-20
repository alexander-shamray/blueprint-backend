namespace Common.Contracts.Payments.V1;

/// <summary>
/// The bounds a payment's provider-minted strings satisfy, published so the
/// service that mints one and the service that records it hold one number.
/// Here because §4.3 lets only this assembly cross a service boundary, on
/// <see cref="Ordering.V1.OrderLimits"/>'s terms. Ordering's own
/// <c>PaymentReference</c> cannot cite it — §4.2 holds that domain to
/// <c>Common.Domain</c> — so a test in Ordering's suite binds the two.
/// </summary>
public static class PaymentLimits
{
    /// <summary>
    /// The longest <see cref="PaymentAuthorised.Reference"/> a provider may
    /// mint and this platform still record. A longer one is authorised money
    /// the order cannot be confirmed against, because the receiving mapper
    /// refuses the message rather than the payment.
    /// </summary>
    public const int MaxReferenceLength = 100;
}
