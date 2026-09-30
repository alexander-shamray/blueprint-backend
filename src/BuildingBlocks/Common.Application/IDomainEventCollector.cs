using Common.Domain;

namespace Common.Application;

/// <summary>§7.5's port over the change tracker, so Application never sees EF Core.</summary>
public interface IDomainEventCollector
{
    /// <summary>Clears as it collects, so a second call returns only events raised since.</summary>
    IReadOnlyList<IDomainEvent> CollectAndClear();
}
