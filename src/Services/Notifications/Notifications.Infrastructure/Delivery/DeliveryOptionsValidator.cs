using System.ComponentModel.DataAnnotations;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Runs <see cref="DeliveryOptions"/>' annotations, then refuses an age the inbox cannot cover.</summary>
internal sealed class DeliveryOptionsValidator(RetentionPolicy retention) : IValidateOptions<DeliveryOptions>
{
    public ValidateOptionsResult Validate(string? name, DeliveryOptions options)
    {
        List<ValidationResult> annotations = [];
        if (!Validator.TryValidateObject(options, new ValidationContext(options), annotations, true))
            return ValidateOptionsResult.Fail(annotations.Select(a => a.ErrorMessage ?? "Invalid."));

        // §9.5: a message replayed from _error inside the give-up age must still meet its inbox row.
        if (retention.InboxWindow < options.GiveUpAge)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(RetentionPolicy)}.{nameof(RetentionPolicy.InboxWindow)}, {retention.InboxWindow}, is " +
                $"shorter than {DeliveryOptions.SectionName}:{nameof(DeliveryOptions.GiveUpAge)}, " +
                $"{options.GiveUpAge}; a notice can wait longer than its redelivery is remembered.");
        }

        return ValidateOptionsResult.Success;
    }
}
