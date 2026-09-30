using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

namespace Common.Application;

/// <summary>What a service's <c>Add&lt;Service&gt;Application</c> composes, less the behaviours (§6.3).</summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>Scoped, like the handlers; here because <c>Dispatcher</c> is internal (§6.2).</summary>
        public IServiceCollection AddDispatcher()
        {
            services.AddScoped<IDispatcher, Dispatcher>();
            return services;
        }

        /// <summary>§7.5's dispatcher and the projection registry behind it, both internal to this assembly.</summary>
        public IServiceCollection AddDomainEventDispatcher()
        {
            // Singleton: the memo is keyed to the container, not to the scope that first asked.
            services.AddSingleton<ProjectionRegistryCache>();
            services.AddScoped<IProjectionRegistry, ProjectionRegistry>();
            services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
            return services;
        }

        /// <summary>
        /// Registers each <see cref="PluggableInterfaces"/> implementation in <paramref name="assembly"/>.
        /// </summary>
        /// <remarks>Each layer calls this, because handlers do not all live in Application (§6.2).</remarks>
        public IServiceCollection AddPluggableFrom(Assembly assembly) =>
            services.Scan(scan =>
            {
                IImplementationTypeSelector from = scan.FromAssemblies(assembly);

                foreach (Type contract in PluggableInterfaces.All)
                {
                    from
                        .AddClasses(c => c.AssignableTo(contract))
                        .AsImplementedInterfaces()
                        .WithScopedLifetime();
                }
            });
    }
}
