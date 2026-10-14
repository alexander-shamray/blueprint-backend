using Common.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Intake;

namespace Notifications.Application;

/// <summary>The one registration method this layer exposes (§4.2), and the assembly's <c>typeof</c> anchor.</summary>
public static class DependencyInjection
{
    /// <param name="host">
    /// The API host keeps only the handlers it serves (ADR-118): the worker's need the bus and the stores only the
    /// worker registers, which <c>ValidateOnBuild</c> would refuse to find in the second process.
    /// </param>
    public static IServiceCollection AddNotificationsApplication(
        this IServiceCollection services,
        NotificationsHost host)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        if (host == NotificationsHost.Api)
            KeepOnlyTheApisHandlers(services);

        services.AddDispatcher();

        // §7.5's dispatcher for a service §4.1 gives no Domain project, where nothing raises an event.
        services.AddScoped<IDomainEventDispatcher, NoDomainEventDispatcher>();

        // The three order events record, then ask the audit rows whether their customer was erased (ADR-097).
        if (host == NotificationsHost.Worker)
            services.AddScoped<ErasedIntake>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // ADR-052's two freshness numbers, registered rather than const so the service can change them.
        services.AddSingleton(new ContactOptions());

        // Ordered and explicit: registration order is pipeline order (§6.3). Idempotency sits inside validation,
        // so a malformed command claims no key, and outside the transaction, so the claim precedes any work.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // The key those two behaviours share, scoped because a command is. Its absence fails the first command,
        // not startup, because ValidateOnBuild never constructs an open generic.
        services.AddScoped<IdempotencyContext>();

        // E.2.6's sample line, over the assembly until the first validator gives it a type.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        return services;
    }

    /// <summary>The namespaces whose handlers the API host serves: ADR-117's device rows.</summary>
    public static readonly IReadOnlyList<string> ApiNamespaces = ["Notifications.Application.Devices"];

    private static void KeepOnlyTheApisHandlers(IServiceCollection services)
    {
        ServiceDescriptor[] others =
        [
            .. services.Where(d =>
                d.ServiceType.IsGenericType &&
                PluggableInterfaces.All.Contains(d.ServiceType.GetGenericTypeDefinition()) &&
                !ApiNamespaces.Any(ns =>
                    d.ImplementationType?.Namespace?.StartsWith(ns, StringComparison.Ordinal) is true))
        ];

        foreach (ServiceDescriptor descriptor in others)
            services.Remove(descriptor);
    }
}
