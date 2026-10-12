using Common.Domain;

namespace Ordering.Application.Privacy;

/// <summary>The audit rows of §11.7's erasure, looked up by the request so a reissue finds its own (ADR-092).</summary>
public interface IPersonalDataErasureRepository
{
    Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>The requests this holder has already erased the subject under, for an order to carry (ADR-099).</summary>
    /// <remarks>The hash is salted by its request, so each row is tried in turn.</remarks>
    Task<IReadOnlyList<Guid>> RequestsErasingAsync(Guid subjectId, CancellationToken ct);

    void Add(PersonalDataErasure erasure);
}
