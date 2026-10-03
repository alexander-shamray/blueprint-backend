using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's give-up age for a notice still pending, past which it is <c>Undeliverable: gave_up</c>.</summary>
/// <remarks>
/// Configuration, as an operator lengthens it during an outage (ADR-052); one age covers every wait at once — the
/// order record, the contact and the relay — since the customer's lateness is the same for all three.
/// </remarks>
public sealed class DeliveryOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Delivery";

    /// <summary>Shorter, and an outage the pass's backoff rides out turns notices terminal.</summary>
    public const string MinimumGiveUpAge = "01:00:00";

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    public const string MaximumGiveUpAge = "3650.00:00:00";

    /// <summary>Measured from the row's <c>CreatedAt</c>; nullable so <c>[Required]</c> sees a missing key.</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumGiveUpAge, MaximumGiveUpAge, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? GiveUpAge { get; init; }
}
