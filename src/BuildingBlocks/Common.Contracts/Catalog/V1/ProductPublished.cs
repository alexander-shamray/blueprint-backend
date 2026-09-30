namespace Common.Contracts.Catalog.V1;

/// <summary>Every contract writes its envelope out rather than inheriting it (§9.2).</summary>
public sealed record ProductPublished : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required string Name { get; init; }

    public required string? ThumbnailUrl { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
