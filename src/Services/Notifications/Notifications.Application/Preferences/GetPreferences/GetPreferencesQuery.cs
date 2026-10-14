using Common.Application;

namespace Notifications.Application.Preferences.GetPreferences;

/// <summary>Every topic and channel pair of the subject's, as the preferences read shows them (ADR-120).</summary>
public sealed record GetPreferencesQuery(Guid SubjectId) : IQuery<IReadOnlyList<PreferenceState>>;
