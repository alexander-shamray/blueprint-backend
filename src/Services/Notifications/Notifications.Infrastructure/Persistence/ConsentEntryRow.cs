namespace Notifications.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlConsentJournal"/>'s table (ADR-120).</summary>
/// <remarks>
/// Written once and never updated but by an erasure, which clears the subject and stamps <see cref="AnonymisedAt"/>;
/// <see cref="Position"/> orders a subject's pair, so a deleted entry never leaves an older one reading as newest.
/// </remarks>
internal sealed class ConsentEntryRow
{
    public Guid EntryId { get; set; }
    public Guid? SubjectId { get; set; }
    public string Topic { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool OptedIn { get; set; }
    public DateTimeOffset WrittenAt { get; set; }
    public string Client { get; set; } = "";
    public Guid? SupersedesEntryId { get; set; }
    public int Position { get; set; }
    public DateTimeOffset? AnonymisedAt { get; set; }
}
