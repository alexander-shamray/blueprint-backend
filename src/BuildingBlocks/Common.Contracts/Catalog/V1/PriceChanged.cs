namespace Common.Contracts.Catalog.V1;

/// <summary>Carries the amount because Ordering prices from its projection of it (§3.2, §6.4).</summary>
public sealed record PriceChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
