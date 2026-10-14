using FluentValidation;
using Microsoft.Extensions.Options;

namespace Web.Bff.Devices;

/// <summary>A <c>PUT</c>'s body (§10.10): the installation's provider, app and token, and its sequence.</summary>
/// <remarks>The token is personal data (ADR-117): never logged, traced or quoted in a refusal (§13.4).</remarks>
public sealed record DeviceRegistration(string Platform, string AppId, string Token, long Sequence);

/// <summary>A <c>DELETE</c>'s one input, from its query string.</summary>
public sealed record DeviceSignOut(long Sequence);

/// <summary>ADR-117's refusals before the hop, so a request that cannot succeed spends none of §9.7's budget.</summary>
/// <remarks>No message quotes a value, so no refusal echoes a token (§13.4).</remarks>
internal sealed class DeviceRegistrationValidator : AbstractValidator<DeviceRegistration>
{
    public DeviceRegistrationValidator(IOptions<PushOptions> push)
    {
        IReadOnlyList<PushApp> apps = [.. push.Value.Apps];

        RuleFor(r => r.Platform)
            .Must(p => DeviceRules.Platforms.Contains(p))
            .WithMessage("must be fcm or apns.");

        // Its platform too, since the deployment names an app with the provider its tokens belong to.
        RuleFor(r => r.AppId)
            .Must((r, appId) => apps.Any(app =>
                string.Equals(app.AppId, appId, StringComparison.Ordinal) &&
                string.Equals(app.Platform, r.Platform, StringComparison.Ordinal)))
            .WithMessage("is not an app this deployment pushes to on that platform.");

        RuleFor(r => r.Token)
            .Must(DeviceRules.IsPrintableAscii)
            .WithMessage("must be printable ASCII.")
            .MaximumLength(DeviceRules.MaxTokenLength)
            .WithMessage($"must be at most {DeviceRules.MaxTokenLength} characters.");

        RuleFor(r => r.Sequence).GreaterThan(0).WithMessage("must be a positive integer.");
    }
}

/// <summary>A sign-out's one check; the installation id is the route's, which binds only a UUID.</summary>
internal sealed class DeviceSignOutValidator : AbstractValidator<DeviceSignOut>
{
    public DeviceSignOutValidator() =>
        RuleFor(r => r.Sequence).GreaterThan(0).WithMessage("must be a positive integer.");
}
