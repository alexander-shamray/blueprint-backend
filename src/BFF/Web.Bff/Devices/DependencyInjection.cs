using FluentValidation;
using Microsoft.Extensions.Options;
using DevicesGrpc = Notifications.Devices.V1.Devices;

namespace Web.Bff.Devices;

/// <summary>E.6.13's registration: the app list, the validators and the hop to Notifications (ADR-117).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddDeviceRegistration(this IServiceCollection services)
    {
        // Validated at start (§15.4): an app list a registration cannot be checked against refuses to boot.
        services
            .AddOptions<PushOptions>()
            .BindConfiguration(PushOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PushOptions>, PushOptionsValidator>();

        services.AddSingleton<IValidator<DeviceRegistration>, DeviceRegistrationValidator>();
        services.AddSingleton<IValidator<DeviceSignOut>, DeviceSignOutValidator>();

        // The hop's numbers and the caller's token, not this host's: Notifications registers whom the token names.
        services.AddNotificationsClient<DevicesGrpc.DevicesClient>(NotificationsHop.ClientName);

        return services;
    }
}
