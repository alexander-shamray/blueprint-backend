namespace Shipping.Application.Carrier;

/// <summary>
/// What an address may carry and still be stored. The columns of
/// <c>shipping.DeliveryAddresses</c> are these widths, and the address adapter
/// refuses a longer answer before a row is written rather than at the insert.
/// </summary>
public static class AddressLimits
{
    public const int MaxLineLength = 200;

    public const int MaxCityLength = 100;

    public const int MaxPostalCodeLength = 32;

    /// <summary>ISO 3166-1 alpha-2: exactly this many letters, never fewer.</summary>
    public const int CountryLength = 2;
}
