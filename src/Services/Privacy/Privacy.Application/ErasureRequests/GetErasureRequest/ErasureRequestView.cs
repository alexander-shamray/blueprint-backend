namespace Privacy.Application.ErasureRequests.GetErasureRequest;

/// <summary>What the status route shows of a request, and never the subject (ADR-092).</summary>
public sealed record ErasureRequestView(
    Guid RequestId,
    string Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ClosedAt,
    int Reissues,
    IReadOnlyList<string> Responders,
    IReadOnlyList<string> Missing,
    IReadOnlyList<HolderAnswer> Answers,
    IReadOnlyList<ResponderAmendment> Amendments);

/// <summary>One holder's answer; <c>Counted</c> is false for a name outside the request's set.</summary>
public sealed record HolderAnswer(string Responder, int Count, bool Counted, DateTimeOffset ReceivedAt);

/// <summary>One change an operator made to the set, with the staff principal who made it (ADR-136).</summary>
public sealed record ResponderAmendment(
    int Sequence,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    string Reason,
    Guid AmendedBy,
    DateTimeOffset AmendedAt);
