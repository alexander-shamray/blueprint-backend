using Notifications.Application.Preferences.SetPreference;

namespace Notifications.Application.Preferences;

/// <summary>ADR-120's append-only journal: the API appends to it, and the worker reads it at send time.</summary>
public interface IConsentJournal
{
    /// <summary>The subject's newest entry for each pair that has one.</summary>
    Task<IReadOnlyList<ConsentEntry>> NewestAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The subject's newest entry for one pair, or null when it has none.</summary>
    Task<ConsentEntry?> NewestAsync(Guid subjectId, ConsentPair pair, CancellationToken ct);

    /// <summary>
    /// Appends while the entry the command read is the pair's newest, in the unit's transaction, stamped with the
    /// erasure requests already applied to the subject (ADR-138).
    /// </summary>
    Task<ConsentAppend> AppendAsync(
        SetPreferenceCommand command,
        IReadOnlyList<Guid> erasedUnder,
        DateTimeOffset now,
        CancellationToken ct);
}

/// <summary>What an append did, and the pair's newest entry after it, null only when the pair has none.</summary>
public sealed record ConsentAppend(ConsentAppendOutcome Outcome, ConsentEntry? Newest);

public enum ConsentAppendOutcome
{
    /// <summary>The entry the request read was the newest, and a new one now supersedes it.</summary>
    Appended,

    /// <summary>The newest entry holds the value asked for and supersedes what the request read.</summary>
    Repeated,

    /// <summary>Another write landed since the request's read; nothing was appended.</summary>
    PreconditionFailed
}
