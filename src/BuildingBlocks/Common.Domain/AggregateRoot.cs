namespace Common.Domain;

/// <summary>Non-generic, so §6.3's one-aggregate assertion can test <c>is IAggregateRoot</c>.</summary>
public interface IAggregateRoot;

/// <summary>The consistency boundary a transaction may span (§5.1).</summary>
public abstract class AggregateRoot<TId>
    : Entity<TId>, IAggregateRoot, IHasDomainEvents
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>Maps to <c>rowversion</c>; empty rather than null, which would fault on the first update.</summary>
    public byte[] Version { get; private set; } = [];
}
