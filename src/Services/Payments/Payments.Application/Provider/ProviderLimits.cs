namespace Payments.Application.Provider;

/// <summary>
/// What a verdict may carry and still be recorded. The reference is confirmed
/// onto the order, so Ordering's PaymentReference.MaxLength is the bound it
/// must fit; the adapter refuses a longer answer and the columns are this wide.
/// </summary>
public static class ProviderLimits
{
    public const int MaxReferenceLength = 100;
    public const int MaxReasonLength = 100;
}
