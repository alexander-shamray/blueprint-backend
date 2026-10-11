namespace Privacy.Application.ErasureRequests;

/// <summary>
/// A subject's or a request's lock was not taken: still held when the wait ran out, or the wait ended
/// as a deadlock victim.
/// </summary>
/// <remarks>
/// An exception, not an <c>Error</c>: no rule decided anything, and a consumer's retry must see a fault.
/// Privacy.Api answers it 503 with <c>Retry-After</c> (§10.5).
/// </remarks>
public sealed class ErasureLockNotTakenException(string resource, Exception inner)
    : Exception($"The erasure lock {resource} was not taken in time.", inner);
