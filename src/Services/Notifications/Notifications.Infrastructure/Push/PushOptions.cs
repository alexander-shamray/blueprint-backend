namespace Notifications.Infrastructure.Push;

/// <summary>The apps this deployment pushes to, each with its provider: the list ADR-117's BFF reads.</summary>
/// <remarks>
/// A platform no app names has no provider registered and needs no provider options, so a deployment may run one
/// provider or none. The BFF's chart value and this one name the same list.
/// </remarks>
public sealed class PushOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Push";

    public const string AppsKey = $"{SectionName}:{nameof(Apps)}";

    public IList<PushApp> Apps { get; init; } = [];

    /// <summary>Whether any app names <paramref name="platform"/>, which is what registers its provider.</summary>
    public bool Names(string platform) =>
        Apps.Any(app => string.Equals(app.Platform, platform, StringComparison.Ordinal));
}

/// <summary>One app: its bundle id or package name, and the provider its tokens belong to.</summary>
public sealed class PushApp
{
    public string? AppId { get; init; }

    public string? Platform { get; init; }
}
