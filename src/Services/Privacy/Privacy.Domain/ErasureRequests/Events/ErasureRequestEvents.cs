using Common.Domain;

namespace Privacy.Domain.ErasureRequests.Events;

/// <summary>A request was raised; §9.3's mapper publishes it as <c>PersonalDataDeleteRequested</c>.</summary>
public sealed record ErasureRequestedDomainEvent(Guid RequestId, Guid SubjectId, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>A request closed; §9.3's mapper publishes it as <c>PersonalDataDeleteClosed</c> (ADR-139).</summary>
public sealed record ErasureRequestClosedDomainEvent(Guid RequestId, DateTimeOffset OccurredAt) : IDomainEvent;
