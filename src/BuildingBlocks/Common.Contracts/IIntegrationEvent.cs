namespace Common.Contracts;

/// <summary>
/// Implemented by every integration event: three primitives and no domain
/// types, as §9.1 requires of a contract.
/// </summary>
/// <remarks>
/// <see cref="MessageId"/> is the only message id: body, outbox row (§9.4),
/// transport header and the inbox key's id half (§9.5) carry one GUID, so an id
/// minted again in <c>OutboxMessage.Stage</c> would leave a logged id absent
/// from the inbox.
/// <see cref="CorrelationId"/> is the mapper's to decide (§9.3).
/// </remarks>
public interface IIntegrationEvent
{
    Guid MessageId { get; }

    Guid CorrelationId { get; }

    DateTimeOffset OccurredAt { get; }
}
