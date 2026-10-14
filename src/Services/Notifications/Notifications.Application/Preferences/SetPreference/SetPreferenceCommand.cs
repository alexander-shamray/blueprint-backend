using Common.Application;

namespace Notifications.Application.Preferences.SetPreference;

/// <summary>A subject's opt-in or withdrawal for one pair, under the version of it they read (ADR-120).</summary>
/// <param name="ReadVersion">
/// The entry the request read, from <c>If-Match</c>; null for <c>If-None-Match: *</c>, a pair with no entry yet.
/// </param>
/// <param name="Client">The <c>azp</c> claim of the caller's token, which the entry records.</param>
/// <remarks>Not an idempotent command: the version read orders a repeat, so no key is claimed (ADR-058).</remarks>
public sealed record SetPreferenceCommand(
    Guid SubjectId,
    string Topic,
    string Channel,
    bool OptedIn,
    Guid? ReadVersion,
    string Client) : ICommand<Result<PreferenceWrite>>;

/// <summary>What the write did, and the pair as it now stands, which a failed precondition answers with.</summary>
public sealed record PreferenceWrite(ConsentAppendOutcome Outcome, PreferenceState State);
