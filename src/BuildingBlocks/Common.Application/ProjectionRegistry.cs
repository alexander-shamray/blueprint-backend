using System.Collections.Concurrent;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Application;

/// <summary>The registry's memo, a singleton so it lives as long as the container, not the process.</summary>
internal sealed class ProjectionRegistryCache
{
    public ConcurrentDictionary<Type, bool> HasHandler { get; } = new();
}

/// <summary>Derived from the container, so it cannot drift from what is registered (§6.2).</summary>
/// <remarks>Scoped, because handlers are scoped and the root provider refuses them (§7.5).</remarks>
internal sealed class ProjectionRegistry(IServiceProvider services, ProjectionRegistryCache cache)
    : IProjectionRegistry
{
    public bool HasHandler(IDomainEvent domainEvent) =>
        cache.HasHandler.GetOrAdd(
            domainEvent.GetType(),
            type => services.GetServices(typeof(IProjectionHandler<>).MakeGenericType(type)).Any());
}
