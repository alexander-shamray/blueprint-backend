using System.ComponentModel.DataAnnotations;

namespace Shipping.Infrastructure.Retention;

/// <summary>
/// ADR-053 rule 1's one options class for this service: the statutory windows a
/// deployment is given, and nothing else — Shipping renders no customer message,
/// so it holds no language set and no time zone.
/// </summary>
/// <remarks>
/// It passes §15.4's test: both windows are a statute's, so a developer's stack
/// is given ADR-053 rule 2's invented ones and a deployment its own.
/// </remarks>
public sealed class ShippingJurisdictionOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Jurisdiction";

    /// <summary>
    /// The shortest window this class will accept. One second rather than
    /// zero, because a zero window deletes the row the instant the shipment
    /// turns terminal and reads on a values file as "not configured".
    /// </summary>
    public const string MinimumWindow = "00:00:01";

    /// <summary>
    /// Ten years, which is a configuration error rather than a policy. Past a
    /// decade the value is a typo, and the purge subtracts it from
    /// <c>DateTimeOffset</c>, which throws where nobody is looking when the
    /// result is not representable.
    /// </summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>
    /// How long a delivery address is kept after its shipment turns terminal
    /// (spec, section 7). Nullable so <c>[Required]</c> can see a key nobody
    /// supplied: a non-nullable <c>TimeSpan</c> binds to <c>00:00:00</c> and
    /// passes the annotation it was given to satisfy.
    /// </summary>
    // The invariant-culture flag is load-bearing: RangeAttribute's string limits
    // go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? AddressRetention { get; init; }

    /// <summary>
    /// How long a shipment's tracking events are kept after delivery. Its own
    /// window and not the address's: one is about a person and the other about
    /// a parcel, and ADR-053's table gives the two clocks separately.
    /// </summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? TrackingRetention { get; init; }
}
