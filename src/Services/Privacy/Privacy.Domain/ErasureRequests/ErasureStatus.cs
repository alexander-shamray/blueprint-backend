namespace Privacy.Domain.ErasureRequests;

/// <summary>Where an erasure request stands; stored by name, never by number (§7.2).</summary>
public enum ErasureStatus
{
    /// <summary>Raised, and waiting on the holders that were expected to answer.</summary>
    Open,

    /// <summary>Open past its due time; nothing closes it by itself, and an operator reissues it (ADR-092).</summary>
    Overdue,

    /// <summary>Every expected holder answered; the subject's id has been replaced by its hash.</summary>
    Closed,
}
