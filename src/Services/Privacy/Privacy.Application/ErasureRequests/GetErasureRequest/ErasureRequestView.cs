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
    IReadOnlyList<HolderAnswer> Answers);

/// <summary>One holder's answer; <c>Counted</c> is false for a name outside the set the request was raised with.</summary>
public sealed record HolderAnswer(string Responder, int Count, bool Counted, DateTimeOffset ReceivedAt);
