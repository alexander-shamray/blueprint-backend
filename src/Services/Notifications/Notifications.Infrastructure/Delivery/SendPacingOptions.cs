using System.ComponentModel.DataAnnotations;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Push;

namespace Notifications.Infrastructure.Delivery;

/// <summary>How hard the send and push loops press the relay and the push providers, which models their rate.</summary>
/// <remarks>
/// The defaults are the workers' own constants and every deployment runs on them; only ADR-157's stress override
/// sets the section, so that a stress run measures this host rather than the providers it paces itself to.
/// </remarks>
public sealed class SendPacingOptions : IValidatableObject
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "SendPacing";

    /// <summary>Past this a batch is a configuration error rather than a pace.</summary>
    public const int MaximumClaimBatchSize = 1000;

    /// <summary>A mail row's two calls, in turn; a pass makes every row's at once, so this bounds a pass.</summary>
    public static readonly TimeSpan SendRowBudget = MailHop.TotalTimeout + ContactHop.TotalRequestTimeout;

    /// <summary>A delivery's one call, the longer provider's; a pass makes every row's at once.</summary>
    public static readonly TimeSpan PushRowBudget =
        FcmHop.TotalTimeout > ApnsHop.TotalTimeout ? FcmHop.TotalTimeout : ApnsHop.TotalTimeout;

    [Range(1, MaximumClaimBatchSize)]
    public int SendClaimBatchSize { get; init; } = SendWorker.ClaimBatchSize;

    public int SendLeaseSeconds { get; init; } = SendWorker.LeaseSeconds;

    public TimeSpan SendTick { get; init; } = MailHop.SendTick;

    [Range(1, MaximumClaimBatchSize)]
    public int PushClaimBatchSize { get; init; } = PushSendWorker.ClaimBatchSize;

    public int PushLeaseSeconds { get; init; } = PushSendWorker.LeaseSeconds;

    public TimeSpan PushTick { get; init; } = PushSendWorker.Tick;

    /// <summary>Refuses a lease a row can outlive, and a pace the relay's breaker could not see.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Rows are sent at once, so one row's calls bound the pass whatever the batch.
        if (TimeSpan.FromSeconds(SendLeaseSeconds) <= SendRowBudget)
        {
            yield return new ValidationResult(
                $"{nameof(SendLeaseSeconds)} must outlive one row's calls of {SendRowBudget}, or a second " +
                "replica claims a row still being sent.",
                [nameof(SendLeaseSeconds)]);
        }

        if (TimeSpan.FromSeconds(PushLeaseSeconds) <= PushRowBudget)
        {
            yield return new ValidationResult(
                $"{nameof(PushLeaseSeconds)} must outlive one provider call of {PushRowBudget}, or a second " +
                "replica pushes a delivery still being sent.",
                [nameof(PushLeaseSeconds)]);
        }

        if (SendTick <= TimeSpan.Zero || PushTick <= TimeSpan.Zero)
            yield return new ValidationResult("A tick must be positive.", [nameof(SendTick), nameof(PushTick)]);

        // MailHop's breaker at the configured pace: a send no retry repeats must still open the breaker alone.
        else if (SendTick * MailHop.CircuitBreakerMinimumThroughput >= MailHop.CircuitBreakerSamplingDuration)
        {
            yield return new ValidationResult(
                $"{nameof(SendTick)} is too slow for the relay's breaker to open inside its sampling window.",
                [nameof(SendTick)]);
        }
    }
}
