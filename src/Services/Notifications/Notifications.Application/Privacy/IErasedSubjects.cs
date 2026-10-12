namespace Notifications.Application.Privacy;

/// <summary>Whether Notifications has erased a subject, read from the audit rows that hold only a hash (ADR-092).</summary>
public interface IErasedSubjects
{
    /// <summary>True when an audit row's hash is the subject's under that row's own request id.</summary>
    /// <param name="subjectId">The customer an event names.</param>
    /// <param name="placedAfter">
    /// The requests the order's customer was already erased under when it was placed, whose rows do not count
    /// (ADR-099).
    /// </param>
    Task<bool> IsErasedAsync(Guid subjectId, IReadOnlyCollection<Guid> placedAfter, CancellationToken ct);
}
