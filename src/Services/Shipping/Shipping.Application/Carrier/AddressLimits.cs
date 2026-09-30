namespace Shipping.Application.Carrier;

/// <summary>The contact table's widths, which the address adapter enforces before a row is written.</summary>
public static class AddressLimits
{
    public const int MaxLineLength = 200;

    public const int MaxCityLength = 100;

    public const int MaxPostalCodeLength = 32;

    /// <summary>ISO 3166-1 alpha-2: exactly this many letters.</summary>
    public const int CountryLength = 2;
}
