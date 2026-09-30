using System.ComponentModel.DataAnnotations;
using Shipping.Infrastructure.Tracking;

namespace Shipping.Infrastructure.Fulfilment;

/// <summary>ADR-052's give-up age, sized to reach <c>OrderFulfilmentSaga.DespatchTimeoutDelay</c>.</summary>
/// <remarks>Configuration, as that deadline is another service's; ADR-054 ends a cancellation at this age.</remarks>
public sealed class FulfilmentOptions : IValidatableObject
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Fulfilment";

    /// <summary>Shorter, and an outage the pass's backoff rides out turns shipments terminal.</summary>
    public const string MinimumGiveUpAge = "01:00:00";

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    public const string MaximumGiveUpAge = "3650.00:00:00";

    /// <summary>From <c>CreatedAt</c> when pending, from <c>CancellationRequestedAt</c> for a cancellation.</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumGiveUpAge, MaximumGiveUpAge, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? GiveUpAge { get; init; }

    /// <summary>Refuses an age at or past <c>TrackingWorker.GiveUpAge</c>, measured alike (ADR-054).</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (GiveUpAge >= TrackingWorker.GiveUpAge)
        {
            yield return new ValidationResult(
                $"{nameof(GiveUpAge)} must be shorter than the tracking age of {TrackingWorker.GiveUpAge}.",
                [nameof(GiveUpAge)]);
        }
    }
}
