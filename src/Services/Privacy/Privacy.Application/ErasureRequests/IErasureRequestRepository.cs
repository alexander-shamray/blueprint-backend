using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests;

/// <summary>§5.6's repository for <see cref="ErasureRequest"/>.</summary>
public interface IErasureRequestRepository
{
    /// <summary>
    /// The request still carrying this subject's id, which is every request that has not closed: a closed one has
    /// handed the id over to the hash its holders' rows carry (ADR-092).
    /// </summary>
    Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct);

    void Add(ErasureRequest request);
}
