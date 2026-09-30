namespace Common.Application;

/// <summary>Stages a message on the command's transaction, for delivery after it commits (§9.3).</summary>
/// <remarks>Called by <c>DomainEventDispatcher</c> only; a saga's own outbox is the one exemption (ADR-032).</remarks>
public interface IIntegrationEventPublisher
{
    Task StageAsync(object message, OutboxLane lane, CancellationToken ct);
}
