namespace Privacy.Domain.ErasureRequests;

/// <summary>Where an erasure request stands; stored by name, never by number (§7.2).</summary>
public enum ErasureStatus
{
    /// <summary>Raised, and waiting on the holders that were expected to answer.</summary>
    Open,
}
