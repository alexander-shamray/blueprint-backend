namespace Privacy.Domain.ErasureRequests;

/// <summary>One operator's change to a request's responder set: what it added and removed, who, when and why.</summary>
/// <remarks>Kept with its request, closed or not, as the record of why the set is not the one raised.</remarks>
public sealed class ErasureAmendment
{
    /// <summary>The amendment's place on its request, from one.</summary>
    public int Sequence { get; private set; }

    /// <summary>The names added, joined by commas as the set is; empty when none were.</summary>
    public string Added { get; private set; } = "";

    /// <summary>The names removed, joined the same way; empty when none were.</summary>
    public string Removed { get; private set; } = "";

    public string Reason { get; private set; } = "";

    /// <summary>The id of the staff user who made the change, the authenticated caller.</summary>
    public Guid AmendedBy { get; private set; }

    public DateTimeOffset AmendedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private ErasureAmendment() { }

    internal ErasureAmendment(
        int sequence,
        IEnumerable<string> added,
        IEnumerable<string> removed,
        string reason,
        Guid amendedBy,
        DateTimeOffset amendedAt)
    {
        Sequence = sequence;
        Added = string.Join(',', added);
        Removed = string.Join(',', removed);
        Reason = reason;
        AmendedBy = amendedBy;
        AmendedAt = amendedAt;
    }
}
