namespace Payments.Application.Provider;

/// <summary>
/// What a verdict may carry and still be recorded. The reference is confirmed
/// onto the order, so Ordering.Domain.Orders.PaymentReference.MaxLength is the
/// bound it must fit, and the adapter refuses a longer answer.
/// </summary>
public static class ProviderLimits
{
    public const int MaxReferenceLength = 100;
    public const int MaxReasonLength = 100;
}
