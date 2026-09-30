using Common.Domain;

namespace Ordering.Domain.Orders;

/// <summary>The payment provider's handle on a settled payment, carried on <c>OrderConfirmedDomainEvent</c>.</summary>
/// <remarks>A string of checked presence and length only, since providers do not agree on a shape (§5.3).</remarks>
public readonly record struct PaymentReference
{
    // The column width (§7.2) and the width Payments may mint; restated because §4.2 keeps this off Common.Contracts.
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
