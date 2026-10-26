using System.ComponentModel.DataAnnotations;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;

namespace Shipping.Infrastructure.Carrier;

/// <summary>How hard the two carrier loops press the carrier, which models its capacity (ADR-157).</summary>
/// <remarks>
/// The defaults are the constants they replace, and every deployment runs on them; only ADR-157's stress override
/// sets the section, so that a stress run measures this host rather than the carrier it paces itself to.
/// </remarks>
public sealed class CarrierPacingOptions : IValidatableObject
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "CarrierPacing";

    /// <summary>Past this a batch is a configuration error rather than a pace.</summary>
    public const int MaximumClaimBatchSize = 1000;

    /// <summary>One fulfilment row's longest turn: the address read, the booking, and a hand-back's cancel.</summary>
    public static readonly TimeSpan FulfilmentRowBudget =
        AddressHop.TotalRequestTimeout + (2 * CarrierHop.TotalRequestTimeout);

    [Range(1, MaximumClaimBatchSize)]
    public int FulfilmentClaimBatchSize { get; init; } = FulfilmentWorker.ClaimBatchSize;

    public int FulfilmentLeaseSeconds { get; init; } = FulfilmentWorker.LeaseSeconds;

    public TimeSpan FulfilmentTick { get; init; } = CarrierHop.FulfilmentTick;

    [Range(1, MaximumClaimBatchSize)]
    public int TrackingClaimBatchSize { get; init; } = TrackingWorker.ClaimBatchSize;

    public int TrackingLeaseSeconds { get; init; } = TrackingWorker.LeaseSeconds;

    public TimeSpan TrackingTick { get; init; } = CarrierHop.TrackingTick;

    public TimeSpan TrackingPollInterval { get; init; } = CarrierHop.TrackingPollInterval;

    /// <summary>Refuses a lease a pass can outlive, and a pace the loops cannot keep.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Rows are booked in turn, so the batch's last finishes a batch of turns after the claim leased them all.
        if (TimeSpan.FromSeconds(FulfilmentLeaseSeconds) <= FulfilmentRowBudget * FulfilmentClaimBatchSize)
        {
            yield return new ValidationResult(
                $"{nameof(FulfilmentLeaseSeconds)} must outlive {FulfilmentClaimBatchSize} rows of " +
                $"{FulfilmentRowBudget} each, or a second replica claims a row this one is still booking.",
                [nameof(FulfilmentLeaseSeconds)]);
        }

        // Rows are polled at once, so one hop bounds the pass whatever the batch.
        if (TimeSpan.FromSeconds(TrackingLeaseSeconds) <= CarrierHop.TotalRequestTimeout)
        {
            yield return new ValidationResult(
                $"{nameof(TrackingLeaseSeconds)} must outlive one carrier hop of {CarrierHop.TotalRequestTimeout}.",
                [nameof(TrackingLeaseSeconds)]);
        }

        if (FulfilmentTick <= TimeSpan.Zero || TrackingTick <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                "A tick must be positive.",
                [nameof(FulfilmentTick), nameof(TrackingTick)]);
        }

        // Whole seconds reach the backoff's floor, so a shorter interval would floor it at nothing.
        if (TrackingPollInterval < TimeSpan.FromSeconds(1))
        {
            yield return new ValidationResult(
                $"{nameof(TrackingPollInterval)} must be at least a second.",
                [nameof(TrackingPollInterval)]);
        }

        // NextPollAt falls part-way into a tick, so a tick as long as the interval takes a due row a tick late.
        if (TrackingTick >= TrackingPollInterval)
        {
            yield return new ValidationResult(
                $"{nameof(TrackingTick)} must be shorter than {nameof(TrackingPollInterval)}.",
                [nameof(TrackingTick)]);
        }
    }
}
