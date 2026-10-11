namespace Shipping.Application.Privacy;

/// <summary>Whether Shipping has erased a subject, read from the audit rows that hold only a hash (ADR-092).</summary>
public interface IErasedSubjects
{
    /// <summary>True when an audit row from <paramref name="since"/> is the subject's under its request id.</summary>
    /// <remarks>An erasure before <paramref name="since"/> had reached the owner, which no longer names them (ADR-092).</remarks>
    Task<bool> IsErasedAsync(Guid subjectId, DateTimeOffset since, CancellationToken ct);
}
