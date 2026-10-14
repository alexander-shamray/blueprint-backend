using Microsoft.Extensions.Options;

namespace Web.Bff.Devices;

/// <summary>The apps this deployment pushes to, the list ADR-117's check on a registration reads.</summary>
/// <remarks>Empty is a deployment that sends no push, and every registration is then refused (§10.10).</remarks>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    public IList<PushApp> Apps { get; init; } = [];
}

/// <summary>One app: its bundle id or package name, and the provider its tokens belong to.</summary>
public sealed class PushApp
{
    public string AppId { get; init; } = "";

    public string Platform { get; init; } = "";
}

/// <summary>Refuses at start a list a registration could not be checked against (§15.4).</summary>
internal sealed class PushOptionsValidator : IValidateOptions<PushOptions>
{
    public ValidateOptionsResult Validate(string? name, PushOptions options)
    {
        List<string> failures = [];

        foreach (PushApp app in options.Apps)
        {
            if (!DeviceRules.IsAppId(app.AppId))
            {
                failures.Add(
                    $"Push:Apps names an app id that is empty, longer than {DeviceRules.MaxAppIdLength} " +
                    "characters or outside printable ASCII.");
            }

            if (!DeviceRules.Platforms.Contains(app.Platform))
                failures.Add($"Push:Apps gives app '{app.AppId}' the platform '{app.Platform}', not fcm or apns.");
        }

        // Ordinal, as the registration compares it: one app id with two platforms leaves a token's provider unknown.
        foreach (IGrouping<string, PushApp> named in options.Apps
                     .GroupBy(app => app.AppId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            failures.Add($"Push:Apps names the app '{named.Key}' {named.Count()} times.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
