namespace Privacy.Domain.ErasureRequests;

/// <summary>What a holder's completion did to a request.</summary>
public enum CompletionOutcome
{
    /// <summary>A holder in the stored set answered for the first time.</summary>
    Counted,

    /// <summary>A holder already heard from answered again, as a reissue makes it; the larger count is kept.</summary>
    Repeated,

    /// <summary>A name outside the stored set: recorded, flagged and never counted.</summary>
    Unexpected,

    /// <summary>The request had already closed, so there is nothing left to record.</summary>
    Ignored,
}
