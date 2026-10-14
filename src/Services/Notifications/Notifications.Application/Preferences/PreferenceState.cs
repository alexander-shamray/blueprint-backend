namespace Notifications.Application.Preferences;

/// <summary>Where a pair's value comes from: a deployment's duty, the class's default, or the subject's act.</summary>
public enum PreferenceSource
{
    Required,
    Default,
    Set
}

/// <summary>One pair as the subject is shown it and as a send is decided under it (ADR-120).</summary>
/// <remarks><c>Version</c> is the pair's newest entry's id, null with <c>SetAt</c> unless set (§10.11).</remarks>
public sealed record PreferenceState(
    string Topic,
    string Channel,
    bool OptedIn,
    PreferenceSource Source,
    DateTimeOffset? SetAt,
    Guid? Version);
