using Microsoft.Extensions.Options;
using Notifications.Application.Devices;

namespace Notifications.Infrastructure.Push;

/// <summary>Refuses at start an app id named twice or blank, or a platform outside ADR-117's pair.</summary>
internal sealed class PushOptionsValidator : IValidateOptions<PushOptions>
{
    public ValidateOptionsResult Validate(string? name, PushOptions options)
    {
        List<string> failures = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        // No message echoes a configured value, as a failed start is logged; the index says which one.
        for (int i = 0; i < options.Apps.Count; i++)
        {
            PushApp app = options.Apps[i];

            if (string.IsNullOrWhiteSpace(app.AppId) || app.AppId.Length > DeviceLimits.MaxAppIdLength ||
                !PrintableAscii.Holds(app.AppId))
            {
                failures.Add($"{PushOptions.AppsKey}:{i}:{nameof(PushApp.AppId)} is not an app id.");
            }
            else if (!seen.Add(app.AppId))
            {
                failures.Add($"{PushOptions.AppsKey}:{i}:{nameof(PushApp.AppId)} names an app already named.");
            }

            if (app.Platform is null || !DeviceLimits.Platforms.Contains(app.Platform, StringComparer.Ordinal))
                failures.Add($"{PushOptions.AppsKey}:{i}:{nameof(PushApp.Platform)} is not fcm or apns.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
