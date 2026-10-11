namespace Shipping.Application.Privacy;

/// <summary>Whether Shipping has erased a subject, read from the audit rows that hold only a hash (ADR-092).</summary>
public interface IErasedSubjects
{
    /// <summary>True when an audit row's hash is the subject's under that row's own request id.</summary>
    Task<bool> IsErasedAsync(Guid subjectId, CancellationToken ct);
}
