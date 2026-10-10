using Common.Application;

namespace Privacy.Application.ErasureRequests.ReissueErasureRequest;

/// <summary>Asks the holders again under the same request id, with a fresh message (ADR-092).</summary>
/// <remarks>Keyed, since a repeat would broadcast again; §6.4: <c>CommandId</c> without the interface protects nothing.</remarks>
public sealed record ReissueErasureRequestCommand(Guid CommandId, Guid RequestId) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "privacy.erasure.reissue";
}
