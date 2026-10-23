using System.ComponentModel.DataAnnotations;
using Common.Infrastructure.Validation;

namespace Shipping.Infrastructure.Retention;

/// <summary>ADR-053's one options class for this service: the statutory windows a deployment is given.</summary>
/// <remarks>Shipping renders no customer message, so it holds no language set and no time zone (ADR-053).</remarks>
public sealed class ShippingJurisdictionOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Jurisdiction";

    /// <summary>One second rather than zero, which reads on a values file as "not configured".</summary>
    public const string MinimumWindow = "00:00:01";

    /// <summary>Ten years, past which a value is a typo the purge's subtraction could throw on.</summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>Kept after the shipment turns terminal; nullable so <c>[Required]</c> sees a missing key.</summary>
    [Required]
    [TimeSpanRange(MinimumWindow, MaximumWindow)]
    public TimeSpan? AddressRetention { get; init; }

    /// <summary>Kept after delivery; its own window, since the address is about a person and this a parcel.</summary>
    [Required]
    [TimeSpanRange(MinimumWindow, MaximumWindow)]
    public TimeSpan? TrackingRetention { get; init; }
}
