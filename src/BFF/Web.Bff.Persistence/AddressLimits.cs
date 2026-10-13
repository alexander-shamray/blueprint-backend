namespace Web.Bff.Persistence;

/// <summary>The saved address's column widths, read by the configuration and by the request's validator.</summary>
/// <remarks>
/// Each equals the width the order's own validator admits, copied because §4.2 lets the BFF reach no service type,
/// so an address that saves is one an order accepts (ADR-107).
/// </remarks>
public static class AddressLimits
{
    public const int Line1MaxLength = 200;

    public const int Line2MaxLength = 200;

    public const int CityMaxLength = 100;

    public const int PostalCodeMaxLength = 20;

    /// <summary>ISO 3166-1 alpha-2's code length.</summary>
    public const int CountryLength = 2;
}
