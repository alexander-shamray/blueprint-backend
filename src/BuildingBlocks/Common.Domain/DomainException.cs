namespace Common.Domain;

/// <summary>A broken invariant: a bug, since validation rejects bad input first (§5.7).</summary>
public class DomainException(string message) : Exception(message);
