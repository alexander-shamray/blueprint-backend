namespace Common.Contracts.Payments.V1;

/// <summary>Bounds shared by the service that mints a provider string and the one that records it (§4.3).</summary>
/// <remarks>Ordering's <c>PaymentReference</c> restates the width: §4.2 keeps its domain off this.</remarks>
public static class PaymentLimits
{
    /// <summary>A longer reference is authorised money the order cannot be confirmed against.</summary>
    public const int MaxReferenceLength = 100;
}
