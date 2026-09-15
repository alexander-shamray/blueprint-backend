namespace Common.Contracts.Catalog.V1;

/// <summary>
/// Catalog's first public fact (§3.2). The namespace carries the version, not
/// the type name (§9.2).
/// </summary>
/// <remarks>
/// The envelope is written out rather than inherited: a shared base is a
/// shared versioning fate (§9.2). <c>Amount</c> and <c>Currency</c> replace
/// <c>Money</c> in the mapper (§9.3), and the source type's
/// <c>*DomainEvent</c> suffix lets §12.4 assert it never reaches the broker.
/// </remarks>
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
