using Common.Domain;

namespace Common.Application;

/// <summary>§9.3's allow-list: an unregistered event is skipped, and a mapper that throws fails the command.</summary>
public interface IIntegrationEventMapper
{
    IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents);
}
