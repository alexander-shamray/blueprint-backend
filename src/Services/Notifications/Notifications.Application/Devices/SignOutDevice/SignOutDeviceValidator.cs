using FluentValidation;

namespace Notifications.Application.Devices.SignOutDevice;

/// <summary>ADR-117's shape checks on a sign-out: a subject, an installation and a positive sequence.</summary>
public sealed class SignOutDeviceValidator : AbstractValidator<SignOutDeviceCommand>
{
    public SignOutDeviceValidator()
    {
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.InstallationId).NotEmpty();
        RuleFor(c => c.Sequence).GreaterThan(0);
    }
}
