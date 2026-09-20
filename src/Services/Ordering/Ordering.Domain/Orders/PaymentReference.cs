using Common.Domain;

namespace Ordering.Domain.Orders;

/// <summary>
/// The payment provider's handle on a settled payment, carried on
/// <c>OrderConfirmedDomainEvent</c> and the one thing that lets a support
/// question about an order reach the provider's own records.
/// </summary>
/// <remarks>
/// A string rather than a <see cref="Guid"/>, because the value is minted
/// outside this platform and providers do not agree on a shape. Validated for
/// presence and length only, for the reason §5.3 gives about postal codes: a
/// guard encoding one provider's format refuses every other provider's valid
/// reference.
/// </remarks>
public readonly record struct PaymentReference
{
    // The column width the reference is stored in (§7.2), and the width
    // Payments may mint. Restated rather than cited because §4.2 keeps this
    // assembly off Common.Contracts, where PaymentLimits.MaxReferenceLength
    // owns it.
    public const int MaxLength = 100;

    public string Value { get; }

    private PaymentReference(string value) => Value = value;

    public static PaymentReference Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("A payment reference cannot be blank.");
        if (value.Length > MaxLength)
            throw new DomainException($"A payment reference cannot exceed {MaxLength} characters.");

        return new PaymentReference(value.Trim());
    }

    public override string ToString() => Value;
}
