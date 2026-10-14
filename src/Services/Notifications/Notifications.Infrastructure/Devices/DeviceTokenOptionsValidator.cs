using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Devices;

/// <summary>Runs <see cref="DeviceTokenOptions"/>' annotations, so a value out of bounds stops the host.</summary>
internal sealed class DeviceTokenOptionsValidator : IValidateOptions<DeviceTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, DeviceTokenOptions options)
    {
        List<ValidationResult> annotations = [];

        return Validator.TryValidateObject(options, new ValidationContext(options), annotations, true)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(annotations.Select(a => a.ErrorMessage ?? "Invalid."));
    }
}
