namespace Privacy.Application.ErasureRequests.GetErasureRequest;

/// <summary>What the status route shows of a request, and never the subject (ADR-092).</summary>
public sealed record ErasureRequestView(
    Guid RequestId,
    string Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset DueAt,
    IReadOnlyList<string> Responders);
