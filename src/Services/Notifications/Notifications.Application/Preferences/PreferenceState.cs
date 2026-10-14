namespace Notifications.Application.Preferences;

/// <summary>Where a pair's value comes from: a deployment's duty, the class's default, or the subject's act.</summary>
public enum PreferenceSource
{
    Required,
    Default,
    Set
}

/// <summary>One pair as the subject is shown it and as a send is decided under it (ADR-120).</summary>
/// <remarks><c>Version</c> is the id of the pair's newest entry, null with <c>SetAt</c> unless it was set.</remarks>
public sealed record PreferenceState(
    string Topic,
    string Channel,
    bool OptedIn,
    PreferenceSource Source,
    DateTimeOffset? SetAt,
    Guid? Version);
