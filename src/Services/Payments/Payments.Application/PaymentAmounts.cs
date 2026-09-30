namespace Payments.Application;

/// <summary>Ordering's decimal(19,4) (§7.2), so any total it stores fits, and the provider's minor units.</summary>
public static class PaymentAmounts
{
    public const int Precision = 19;
    public const int Scale = 4;
    public const int MinorUnitPlaces = 2;

    /// <summary>The first amount a decimal(19,4) column cannot hold.</summary>
    public const decimal Ceiling = 1_000_000_000_000_000m;
}
