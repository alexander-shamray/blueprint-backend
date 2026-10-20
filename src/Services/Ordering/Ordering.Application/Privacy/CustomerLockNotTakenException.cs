namespace Ordering.Application.Privacy;

/// <summary>A customer's lock was not taken: held past the wait, or the wait ended as a deadlock victim.</summary>
/// <remarks>
/// An exception, not an <c>Error</c>: no rule decided anything, and a consumer's retry must see a fault.
/// Ordering.Api answers it 503 with <c>Retry-After</c> (§10.5).
/// </remarks>
public sealed class CustomerLockNotTakenException(string resource, Exception inner)
    : Exception($"The customer lock {resource} was not taken in time.", inner);
