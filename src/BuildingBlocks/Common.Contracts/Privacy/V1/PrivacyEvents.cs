namespace Common.Contracts.Privacy.V1;

/// <summary>Every holder of the subject's personal data erases it (§11.7).</summary>
/// <remarks>The subject's id and no other fact about the subject (ADR-092).</remarks>
public sealed record PersonalDataDeleteRequested : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid RequestId { get; init; }

    public required Guid SubjectId { get; init; }
}
