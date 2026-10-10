using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests;

/// <summary>§5.6's repository for <see cref="ErasureRequest"/>.</summary>
public interface IErasureRequestRepository
{
    /// <summary>
    /// The request still carrying this subject's id, which is every request that has not closed: a closed one has
    /// handed the id over to the hash its holders' rows carry (ADR-092). Runs inside the unit of work, since it
    /// takes the subject's lock for the transaction (§6.3).
    /// </summary>
    Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// One request by id, tracked, with its holders' answers. Runs inside the unit of work, since it takes the
    /// request's lock for the transaction: five holders answering at once are taken in turn, not retried (§6.3).
    /// </summary>
    Task<ErasureRequest?> GetLockedAsync(Guid requestId, CancellationToken ct);

    void Add(ErasureRequest request);
}
