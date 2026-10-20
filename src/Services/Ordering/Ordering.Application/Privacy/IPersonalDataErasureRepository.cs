using Common.Domain;

namespace Ordering.Application.Privacy;

/// <summary>The audit rows of §11.7's erasure, looked up by the request so a reissue finds its own (ADR-092).</summary>
public interface IPersonalDataErasureRepository
{
    Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>
    /// Holds the subject until the unit commits, so a placement and an erasure for one customer run one after the
    /// other (ADR-137). Taken by both before they read or write anything the other touches.
    /// </summary>
    /// <exception cref="CustomerLockNotTakenException">The lock stayed held past the wait.</exception>
    Task LockSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>The requests this holder has already erased the subject under, for an order to carry (ADR-099).</summary>
    /// <remarks>The hash is salted by its request (ADR-092), so each row is tried in turn.</remarks>
    Task<IReadOnlyList<Guid>> RequestsErasingAsync(Guid subjectId, CancellationToken ct);

    void Add(PersonalDataErasure erasure);
}
