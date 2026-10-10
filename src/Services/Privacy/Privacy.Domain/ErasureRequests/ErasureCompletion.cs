namespace Privacy.Domain.ErasureRequests;

/// <summary>One holder's answer to a request: how many records it erased, and whether the answer counts.</summary>
/// <remarks>Counted only when the holder is in the set stored on the request; others are kept to be seen (ADR-092).</remarks>
public sealed class ErasureCompletion
{
    public string Responder { get; private set; } = "";

    /// <summary>The most records this holder reported removing in any pass.</summary>
    public int Count { get; private set; }

    /// <summary>False for a name outside the stored set.</summary>
    public bool Counted { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private ErasureCompletion() { }

    internal ErasureCompletion(string responder, int count, bool counted, DateTimeOffset receivedAt)
    {
        Responder = responder;
        Count = count;
        Counted = counted;
        ReceivedAt = receivedAt;
    }

    /// <summary>A reissue erases again and reports again; the record keeps the most it ever removed.</summary>
    internal void Repeat(int count, DateTimeOffset receivedAt)
    {
        Count = Math.Max(Count, count);
        ReceivedAt = receivedAt;
    }
}
