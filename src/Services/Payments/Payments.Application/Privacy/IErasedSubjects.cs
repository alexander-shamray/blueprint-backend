namespace Payments.Application.Privacy;

/// <summary>Whether Payments has erased a subject, read from the audit rows that hold only a hash (ADR-092).</summary>
public interface IErasedSubjects
{
    /// <summary>True when an audit row's hash is the subject's under that row's own request id.</summary>
    /// <param name="placedAfter">The requests the order was placed under, whose rows do not count (ADR-141).</param>
    /// <param name="sourcedAt">No later than the data was taken from its owner; a request that closed well before
    /// it does not count (ADR-139).</param>
    Task<bool> IsErasedAsync(
        Guid subjectId,
        IReadOnlyCollection<Guid> placedAfter,
        DateTimeOffset sourcedAt,
        CancellationToken ct);
}
