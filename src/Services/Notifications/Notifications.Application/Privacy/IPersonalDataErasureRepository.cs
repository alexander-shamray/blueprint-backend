using Common.Domain;

namespace Notifications.Application.Privacy;

/// <summary>The audit rows of §11.7's erasure, looked up by the request so a reissue finds its own (ADR-092).</summary>
public interface IPersonalDataErasureRepository
{
    Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>True once Privacy's close of the request has reached its row here (ADR-139).</summary>
    Task<bool> IsClosedAsync(Guid requestId, CancellationToken ct);

    /// <summary>The requests this holder has already erased the subject under, for a row to carry.</summary>
    /// <param name="sourcedAt">No later than the row took the data from its owner; a request that closed well
    /// before it is not listed (ADR-139).</param>
    /// <remarks>Read before the row is written, so it names only requests that ran first (ADR-138).</remarks>
    Task<IReadOnlyList<Guid>> RequestsErasingAsync(Guid subjectId, DateTimeOffset sourcedAt, CancellationToken ct);

    void Add(PersonalDataErasure erasure);
}
