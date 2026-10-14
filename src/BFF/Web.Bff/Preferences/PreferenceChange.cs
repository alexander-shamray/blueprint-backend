using FluentValidation;

namespace Web.Bff.Preferences;

/// <summary>A <c>PUT</c>'s body (§10.11): whether the subject opts in to the route's pair.</summary>
/// <remarks>Nullable, so a body that omits the member is a 400 naming it rather than a silent withdrawal.</remarks>
public sealed record PreferenceChange(bool? OptedIn);

/// <summary>A write as the route and the body state it, checked as one before the hop.</summary>
public sealed record PreferenceWrite(string Topic, string Channel, bool? OptedIn);

/// <summary>ADR-120's refusals before the hop, so a write that cannot succeed spends none of §9.7's budget.</summary>
internal sealed class PreferenceWriteValidator : AbstractValidator<PreferenceWrite>
{
    public PreferenceWriteValidator()
    {
        RuleFor(w => w.Topic).Must(t => PreferenceRules.Topics.Contains(t)).WithMessage("is not a topic of this API.");
        RuleFor(w => w.Channel).Must(c => PreferenceRules.Channels.Contains(c)).WithMessage("must be email or push.");
        RuleFor(w => w.OptedIn).NotNull().WithMessage("must be true or false.");
    }
}
