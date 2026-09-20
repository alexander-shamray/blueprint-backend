namespace Payments.Application.Provider;

/// <summary>
/// What a verdict may carry and still be recorded. The reference is not here:
/// it is confirmed onto the order, so its width is the published
/// <c>PaymentLimits.MaxReferenceLength</c> and a second copy beside this one
/// is the drift that would authorise money no order could be confirmed
/// against. The reason never leaves this service as a length.
/// </summary>
public static class ProviderLimits
{
    public const int MaxReasonLength = 100;
}
