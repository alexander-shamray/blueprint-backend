using FluentValidation;
using Notifications.Application.Devices;

namespace Notifications.Application.Preferences.SetPreference;

/// <summary>ADR-120's vocabulary, checked here too because the BFF is not all that can reach the method.</summary>
public sealed class SetPreferenceValidator : AbstractValidator<SetPreferenceCommand>
{
    public SetPreferenceValidator()
    {
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.Topic).Must(ConsentVocabulary.IsTopic).WithMessage("is not a topic of this service.");
        RuleFor(c => c.Channel).Must(ConsentVocabulary.IsChannel).WithMessage("must be email or push.");

        // Guid.Empty is no entry's id, so a version naming it is malformed rather than merely stale.
        RuleFor(c => c.ReadVersion).NotEqual(Guid.Empty);

        RuleFor(c => c.Client).NotEmpty().MaximumLength(ConsentVocabulary.MaxClientLength).Must(PrintableAscii.Holds)
            .WithMessage("must be printable ASCII.");
    }
}
