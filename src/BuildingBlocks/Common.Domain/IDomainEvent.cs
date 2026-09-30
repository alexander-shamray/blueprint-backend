namespace Common.Domain;

/// <summary>Scoped to one service and free to carry domain types, unlike an integration event (§5.5, §9.1).</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Non-generic, so §7.5 can filter the change tracker by it without a key type.</summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
