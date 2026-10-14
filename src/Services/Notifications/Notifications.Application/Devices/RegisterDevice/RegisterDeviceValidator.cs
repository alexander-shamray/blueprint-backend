using FluentValidation;

namespace Notifications.Application.Devices.RegisterDevice;

/// <summary>ADR-117's shape checks, here too because the BFF is not all that can reach the method.</summary>
/// <remarks>No message quotes a value: a token is never part of an exception's message (§13.4).</remarks>
public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceValidator()
    {
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.InstallationId).NotEmpty();
        RuleFor(c => c.Platform).Must(p => DeviceLimits.Platforms.Contains(p)).WithMessage("must be fcm or apns.");
        RuleFor(c => c.AppId).NotEmpty().MaximumLength(DeviceLimits.MaxAppIdLength).Must(PrintableAscii.Holds)
            .WithMessage("must be printable ASCII.");
        RuleFor(c => c.Token).NotEmpty().MaximumLength(DeviceLimits.MaxTokenLength).Must(PrintableAscii.Holds)
            .WithMessage("must be printable ASCII.");
        RuleFor(c => c.Sequence).GreaterThan(0);
    }
}
