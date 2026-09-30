namespace Common.Contracts;

/// <summary>Three primitives and no domain types (§9.1).</summary>
/// <remarks><see cref="MessageId"/> is the one id body, outbox row and inbox key carry (§9.4, §9.5).</remarks>
public interface IIntegrationEvent
{
    Guid MessageId { get; }

    Guid CorrelationId { get; }

    DateTimeOffset OccurredAt { get; }
}
