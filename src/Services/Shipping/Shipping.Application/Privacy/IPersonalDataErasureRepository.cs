using Common.Domain;

namespace Shipping.Application.Privacy;

/// <summary>The audit rows of §11.7's erasure, looked up by the request so a reissue finds its own (ADR-092).</summary>
public interface IPersonalDataErasureRepository
{
    Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>True once Privacy's close of the request has reached its row here (ADR-139).</summary>
    Task<bool> IsClosedAsync(Guid requestId, CancellationToken ct);

    void Add(PersonalDataErasure erasure);
}
