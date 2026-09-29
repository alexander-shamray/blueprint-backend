using System.ComponentModel.DataAnnotations;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>
/// ADR-052's give-up age: how long a shipment may stay <c>Pending</c> before
/// the fulfilment pass makes it unfulfillable, sized to reach
/// <c>OrderFulfilmentSaga.DespatchTimeoutDelay</c>, at which the saga raises
/// the order for review, give or take the two consumers' lag on the one
/// event both clocks start from. Configuration rather than a constant
/// because that deadline is another service's, and an operator riding out a
/// long outage decides whether a day's shipments wait for it. ADR-054 ends
/// an unanswered cancellation at the same age.
/// </summary>
public sealed class FulfilmentOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Fulfilment";

    /// <summary>
    /// One hour. Shorter, and an outage the pass's own backoff is built to
    /// ride out turns shipments terminal before the ladder has climbed.
    /// </summary>
    public const string MinimumGiveUpAge = "01:00:00";

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    public const string MaximumGiveUpAge = "3650.00:00:00";

    /// <summary>
    /// How long a <c>Pending</c> shipment is retried, measured from
    /// <c>Shipment.CreatedAt</c>, and a cancellation, measured from
    /// <c>Shipment.CancellationRequestedAt</c>. Nullable so <c>[Required]</c>
    /// can see a key nobody supplied, as <c>ShippingJurisdictionOptions</c>'
    /// windows are.
    /// </summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads
    // the current culture unless told otherwise.
    [Required]
    [Range(typeof(TimeSpan), MinimumGiveUpAge, MaximumGiveUpAge, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? GiveUpAge { get; init; }
}
