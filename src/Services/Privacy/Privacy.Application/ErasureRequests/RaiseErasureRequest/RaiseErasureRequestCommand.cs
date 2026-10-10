using Common.Application;

namespace Privacy.Application.ErasureRequests.RaiseErasureRequest;

/// <summary>Answers with the id of the request now open for the subject, which may be one already open.</summary>
/// <remarks>No <c>CommandId</c>: asking again for a subject who has a request open returns that request.</remarks>
public sealed record RaiseErasureRequestCommand(Guid SubjectId) : ICommand<Result<Guid>>;
