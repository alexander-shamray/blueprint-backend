using Common.Domain;

namespace Common.Application;

/// <summary>Whether an event has a projection handler, so no unconsumed <c>Local</c> row is staged (§7.5).</summary>
/// <remarks>Empty means false here, where <c>ProjectionInvoker</c> throws on it (§9.4).</remarks>
public interface IProjectionRegistry
{
    bool HasHandler(IDomainEvent domainEvent);
}
