using Common.Application;

namespace Privacy.Application.ErasureRequests.AmendResponders;

/// <summary>Adds holders to, or removes them from, the set of a request that has not closed (ADR-136).</summary>
/// <remarks>
/// Keyed, since a repeat would record a second amendment; §6.4: <c>CommandId</c> without the interface protects
/// nothing.
/// </remarks>
public sealed record AmendErasureRespondersCommand(
    Guid CommandId,
    Guid RequestId,
    IReadOnlyList<string> Add,
    IReadOnlyList<string> Remove,
    string Reason) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "privacy.erasure.amend-responders";
}
