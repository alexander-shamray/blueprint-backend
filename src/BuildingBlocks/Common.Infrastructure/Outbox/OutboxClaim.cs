namespace Common.Infrastructure.Outbox;

/// <summary>Dapper projection of the claim's <c>OUTPUT</c> clause, which it must match exactly (§9.4).</summary>
/// <remarks><c>Lane</c> is a string, not <see cref="Common.Application.OutboxLane"/>: a bad one fails a row.</remarks>
public sealed record OutboxClaim(
    long Id,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    string Payload,
    string Lane,
    int Attempts,
    DateTimeOffset OccurredAt);
