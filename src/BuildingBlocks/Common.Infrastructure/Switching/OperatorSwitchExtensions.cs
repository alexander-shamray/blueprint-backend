using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Infrastructure.Switching;

/// <summary>The lines a switchable host adds: the services and the bus (ADR-113); the file is Common.Web's.</summary>
public static class OperatorSwitchExtensions
{
    /// <summary>Idempotent, since each host's bus registration calls it and a suite may call that twice.</summary>
    public static IServiceCollection AddOperatorSwitches(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(OperatorSwitches)))
            return services;

        // The configuration the host was given, not the container's, as a suite builds a bus without registering one.
        IConfigurationSection section = configuration.GetSection(OperatorSwitchOptions.SectionName);
        OperatorSwitchSource source = new(section);

        // Refused here rather than by a validator, which a reload would run on the monitor's thread: the health
        // check reports a scalar that arrives later.
        if (source.Scalar() is { } scalar)
            throw new InvalidOperationException(scalar);

        services.AddSingleton(source);
        services
            .AddOptions<OperatorSwitchOptions>()
            .Bind(section);
        services.AddSingleton<OperatorSwitches>();
        services.AddSingleton<ReceiveEndpointSwitch>();

        // Tagged ready so a loop that is off shows in readiness; Degraded keeps the pod in service (ADR-115).
        services
            .AddHealthChecks()
            .AddCheck<OperatorSwitchHealthCheck>(OperatorSwitchHealthCheck.Name, tags: ["ready"]);

        return services;
    }

    /// <summary>Connects the switch to every endpoint configured after it, so a new endpoint is not left out.</summary>
    /// <remarks>Called before the first <c>ReceiveEndpoint</c>, or that one is not seen (ADR-113).
    /// <paramref name="topologyBus"/> builds a bus configured as the host's, applying the action it is given, so a
    /// deferred endpoint's queue and bindings are declared as this bus would declare them (ADR-145).</remarks>
    public static void ConnectOperatorSwitch(
        this IBusFactoryConfigurator cfg,
        IServiceProvider services,
        Func<Action<IBusFactoryConfigurator>, IBusControl> topologyBus)
    {
        ReceiveEndpointSwitch endpointSwitch = services.GetRequiredService<ReceiveEndpointSwitch>();
        endpointSwitch.Attach(services, topologyBus);
        cfg.ConnectEndpointConfigurationObserver(new SwitchEveryEndpoint(endpointSwitch));
        cfg.ConnectBusObserver(new DeclareDeferredOnStart(endpointSwitch));
    }

    /// <summary>
    /// A receive endpoint the operator switch governs: configured now when its switch is on, and otherwise left
    /// unconfigured until the switch connects it, so a pod started while it is off never consumes from it (ADR-144).
    /// </summary>
    public static void SwitchedReceiveEndpoint(
        this IBusFactoryConfigurator cfg,
        IServiceProvider services,
        string queue,
        Action<IReceiveEndpointConfigurator> configure)
    {
        if (services.GetRequiredService<ReceiveEndpointSwitch>().Register(queue, configure))
            cfg.ReceiveEndpoint(queue, configure);
    }

    private sealed class SwitchEveryEndpoint(ReceiveEndpointSwitch endpointSwitch) : IEndpointConfigurationObserver
    {
        public void EndpointConfigured<T>(T configurator)
            where T : IReceiveEndpointConfigurator =>
            endpointSwitch.Configured(configurator);
    }

    // On start, not on configuration: a host test builds the bus without a broker and never starts it. Off the start,
    // since a broker outage would otherwise hold it; each declaration retries on its own.
    private sealed class DeclareDeferredOnStart(ReceiveEndpointSwitch endpointSwitch) : IBusObserver
    {
        public void PostCreate(IBus bus)
        {
        }

        public void CreateFaulted(Exception exception)
        {
        }

        public Task PreStart(IBus bus)
        {
            _ = Task.Run(endpointSwitch.DeclareDeferredAsync);
            return Task.CompletedTask;
        }

        public Task PostStart(IBus bus, Task<BusReady> busReady) => Task.CompletedTask;

        public Task StartFaulted(IBus bus, Exception exception) => Task.CompletedTask;

        public Task PreStop(IBus bus) => Task.CompletedTask;

        public Task PostStop(IBus bus) => Task.CompletedTask;

        public Task StopFaulted(IBus bus, Exception exception) => Task.CompletedTask;
    }
}
