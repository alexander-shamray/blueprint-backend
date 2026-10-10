namespace Common.Contracts.Privacy.V1;

/// <summary>A holder tells Privacy it has erased what it holds for a request (ADR-094).</summary>
/// <remarks>
/// Sent to Privacy's queue, which accepts it from every holder, so it is a command and not an event (§3.2). The
/// <paramref name="Responder"/> is the holder's own claim, checked against the request's stored set.
/// </remarks>
public sealed record PersonalDataDeleteCompleted(Guid RequestId, string Responder, int Count);
