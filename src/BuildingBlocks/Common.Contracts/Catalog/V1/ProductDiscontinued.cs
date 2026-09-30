namespace Common.Contracts.Catalog.V1;

/// <summary>No reason code: §6.6's projection flips <c>IsAvailable</c> either way.</summary>
public sealed record ProductDiscontinued : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }
}
