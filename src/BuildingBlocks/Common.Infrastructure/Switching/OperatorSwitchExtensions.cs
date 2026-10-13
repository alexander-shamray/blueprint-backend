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
        services
            .AddOptions<OperatorSwitchOptions>()
            .Bind(configuration.GetSection(OperatorSwitchOptions.SectionName));
        services.AddSingleton<OperatorSwitches>();
        services.AddSingleton<ReceiveEndpointSwitch>();

        // Tagged ready so a loop that is off shows in readiness; Degraded keeps the pod in service (ADR-115).
        services
            .AddHealthChecks()
            .AddCheck<OperatorSwitchHealthCheck>(OperatorSwitchHealthCheck.Name, tags: ["ready"]);

        return services;
    }

    /// <summary>Connects the switch to every endpoint configured after it, so a new endpoint is not left out.</summary>
    /// <remarks>Called before the first <c>ReceiveEndpoint</c>; one declared earlier would not be seen (ADR-113).</remarks>
    public static void ConnectOperatorSwitch(this IBusFactoryConfigurator cfg, IServiceProvider services) =>
        cfg.ConnectEndpointConfigurationObserver(
            new SwitchEveryEndpoint(services.GetRequiredService<ReceiveEndpointSwitch>()));

    private sealed class SwitchEveryEndpoint(ReceiveEndpointSwitch endpointSwitch) : IEndpointConfigurationObserver
    {
        public void EndpointConfigured<T>(T configurator)
            where T : IReceiveEndpointConfigurator =>
            endpointSwitch.Configured(configurator);
    }
}
