using Common.Domain;

namespace Payments.Application;

/// <summary>Ordering's decimal(19,4) (§7.2), so any total it stores fits, and the provider's minor units.</summary>
public static class PaymentAmounts
{
    public const int Precision = 19;
    public const int Scale = 4;

    /// <summary>The first amount a decimal(19,4) column cannot hold.</summary>
    public const decimal Ceiling = 1_000_000_000_000_000m;

    /// <summary>The amount in its currency's minor units (ADR-067), or null where no whole <c>long</c> is it.</summary>
    public static long? ToMinorUnits(decimal amount, string currency)
    {
        int exponent = CurrencyMinorUnits.Of(currency);
        decimal minor = amount;

        for (int place = 0; place < exponent; place++)
            minor *= 10m;

        return minor == decimal.Truncate(minor) && minor <= long.MaxValue ? decimal.ToInt64(minor) : null;
    }
}
