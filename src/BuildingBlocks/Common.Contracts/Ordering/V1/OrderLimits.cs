namespace Common.Contracts.Ordering.V1;

/// <summary>Enforced by Ordering and the BFF's quote alike; only this assembly crosses (§4.3).</summary>
public static class OrderLimits
{
    public const int MinQuantity = 1;

    /// <summary>Per product, not per line: a validator sums a repeated product first (ADR-045).</summary>
    public const int MaxQuantity = 999;

    /// <summary>Keeps <c>ProjectedPriceReader</c>'s one parameter per product under SQL Server's 2,100.</summary>
    public const int MaxLines = 100;
}
