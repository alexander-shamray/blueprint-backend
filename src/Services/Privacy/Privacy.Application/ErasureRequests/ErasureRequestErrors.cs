using Common.Application;

namespace Privacy.Application.ErasureRequests;

/// <summary>Every <see cref="Error"/> is constructed here, so <c>Code</c> stays closed (§10.5).</summary>
public static class ErasureRequestErrors
{
    public static readonly Error NotFound =
        Error.NotFound("erasure_request.not_found", "No erasure request under that identifier.");
}
