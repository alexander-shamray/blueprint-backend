namespace Payments.Application;

/// <summary>
/// The money this service records and sends. Ordering's money precision
/// (§7.2's decimal(19,4), its saga's <c>Total</c> and every line price), so
/// any total Ordering stores fits here; and two places, the minor units the
/// provider is sent.
/// </summary>
public static class PaymentAmounts
{
    public const int Precision = 19;
    public const int Scale = 4;
    public const int MinorUnitPlaces = 2;

    /// <summary>The first amount a decimal(19,4) column cannot hold.</summary>
    public const decimal Ceiling = 1_000_000_000_000_000m;
}
