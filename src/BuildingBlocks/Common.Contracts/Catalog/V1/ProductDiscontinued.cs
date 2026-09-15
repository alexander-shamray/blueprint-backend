namespace Common.Contracts.Catalog.V1;

/// <summary>
/// A product is no longer offered (§3.2). The envelope plus the id, and
/// nothing else.
/// </summary>
/// <remarks>
/// No reason code, deliberately. §6.6's projection flips
/// <c>IsAvailable</c> either way, so a reason would be a member every consumer
/// must version around and none reads — and the first consumer that did read it
/// would branch on a vocabulary Catalog had never committed to.
/// </remarks>
public sealed record ProductDiscontinued : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }
}
