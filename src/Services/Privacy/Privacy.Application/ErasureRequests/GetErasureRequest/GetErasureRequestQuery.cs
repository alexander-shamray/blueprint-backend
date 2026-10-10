using Common.Application;

namespace Privacy.Application.ErasureRequests.GetErasureRequest;

/// <summary>One request by id, for the operator who raised it (§6.5).</summary>
public sealed record GetErasureRequestQuery(Guid RequestId) : IQuery<Result<ErasureRequestView>>;
