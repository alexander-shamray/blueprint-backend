using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Tests;

/// <summary>A contract for the map to find; §9.4 persists <c>FullName</c>, so the names matter too.</summary>
public sealed record SampleIntegrationEvent : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Note { get; init; }
}

public sealed record SampleDomainEvent(DateTimeOffset OccurredAt, string Note) : IDomainEvent;

/// <summary>A value-type domain event, which no event interface's constraint forbids.</summary>
public readonly record struct SampleValueTypeDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A legal non-ASCII event name, which is why <c>MessageType</c> is nvarchar.</summary>
public sealed record CommandeCréée(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Both at once, the §5.5 mistake C# compiles, so <c>Stage</c> has to refuse it.</summary>
public sealed record Conflated : IDomainEvent, IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Neither, so <c>NameOf</c> must refuse it.</summary>
public sealed record NotAMessage(string Note);
