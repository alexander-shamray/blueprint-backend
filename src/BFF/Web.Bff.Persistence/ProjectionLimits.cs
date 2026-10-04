namespace Web.Bff.Persistence;

/// <summary>The projection's column widths, read by the configurations and by the handlers' refusals.</summary>
public static class ProjectionLimits
{
    /// <summary>ISO 4217's code length, as every service's money column holds it.</summary>
    public const int CurrencyLength = 3;

    /// <summary>Catalog's product-name column width, copied because §4.2 lets the BFF reach no Catalog type.</summary>
    public const int ProductNameMaxLength = 200;

    /// <summary><c>ShipmentLimits.MaxTrackingNumberLength</c>'s value, copied for the same reason.</summary>
    public const int TrackingNumberMaxLength = 64;

    /// <summary>Room for the longest of <see cref="CancelOutcomes"/>' members.</summary>
    public const int CancelOutcomeMaxLength = 16;
}
