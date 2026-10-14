namespace Notifications.Application.Preferences;

/// <summary>One act of a subject's in ADR-120's journal, as a read returns it; the subject is the reader's.</summary>
/// <remarks><c>Client</c> is the <c>azp</c> claim of the token the act was made under.</remarks>
public sealed record ConsentEntry(
    Guid EntryId,
    string Topic,
    string Channel,
    bool OptedIn,
    DateTimeOffset WrittenAt,
    string Client,
    Guid? Supersedes);
