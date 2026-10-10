using Common.Application;

namespace Privacy.Application.ErasureRequests.RecordCompletion;

/// <summary>One holder's answer to a request, as it arrives on Privacy's queue (ADR-094).</summary>
/// <remarks>Idempotent on the request and the holder, and so on the message too, as every consumer is (ADR-092).</remarks>
public sealed record RecordErasureCompletionCommand(Guid RequestId, string Responder, int Count) : ICommand<Result>;
