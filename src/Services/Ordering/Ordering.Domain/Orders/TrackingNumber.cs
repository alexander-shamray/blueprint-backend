using Common.Domain;

namespace Ordering.Domain.Orders;

/// <summary>The carrier's handle on a shipment, carried on <c>OrderShippedDomainEvent</c>.</summary>
/// <remarks>Presence and length only, as <see cref="PaymentReference"/>: carriers differ in format.</remarks>
public readonly record struct TrackingNumber
{
    // The column width it is stored in (§7.2), so the guard and the mapping cannot disagree.
    public const int MaxLength = 100;

    public string Value { get; }

    private TrackingNumber(string value) => Value = value;

    public static TrackingNumber Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("A tracking number cannot be blank.");
        if (value.Length > MaxLength)
            throw new DomainException($"A tracking number cannot exceed {MaxLength} characters.");

        return new TrackingNumber(value.Trim());
    }

    public override string ToString() => Value;
}
