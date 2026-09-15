namespace Common.Domain;

/// <summary>
/// A record that something meaningful happened, in past tense (§5.1). Scoped to
/// one service and free to carry domain types — an integration event is a
/// different thing under a different set of rules (§5.5, §9.1).
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Non-generic marker: EF's change tracker holds objects, and §7.5 filters it
/// with <c>Entries&lt;IHasDomainEvents&gt;()</c> without knowing any key type.
/// </summary>
/// <remarks>
/// Missing from the base class, the query matches nothing and a command commits
/// having staged no outbox rows — no projection, integration event or saga
/// start (§5.5).
/// </remarks>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
