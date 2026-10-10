using Common.Domain;

namespace Privacy.Domain.ErasureRequests.Events;

/// <summary>A request was raised; §9.3's mapper publishes it as <c>PersonalDataDeleteRequested</c>.</summary>
public sealed record ErasureRequestedDomainEvent(Guid RequestId, Guid SubjectId, DateTimeOffset OccurredAt)
    : IDomainEvent;
