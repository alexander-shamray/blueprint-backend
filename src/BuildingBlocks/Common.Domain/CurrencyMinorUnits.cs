namespace Common.Domain;

/// <summary>ISO 4217's minor unit for a currency code, held once for every context's money (ADR-067).</summary>
/// <remarks>Only codes whose exponent is not <see cref="Default"/> are listed; ADR-067 says why the rest take it.</remarks>
public static class CurrencyMinorUnits
{
    public const int Default = 2;

    public static int Of(string currency) => currency.ToUpperInvariant() switch
    {
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF" or "UGX" or "UYI"
            or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        "CLF" or "UYW" => 4,
        _ => Default
    };
}
