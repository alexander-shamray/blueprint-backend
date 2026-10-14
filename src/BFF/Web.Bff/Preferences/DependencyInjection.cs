using FluentValidation;
using PreferencesGrpc = Notifications.Preferences.V1.Preferences;

namespace Web.Bff.Preferences;

/// <summary>§10.11's preferences: the validator, the precondition filter and the hop (ADR-120).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationPreferences(this IServiceCollection services)
    {
        services.AddSingleton<IValidator<PreferenceWrite>, PreferenceWriteValidator>();
        services.AddSingleton<PreconditionFilter>();

        // The hop's numbers and the caller's token: Notifications takes the subject and the client from the token.
        services.AddNotificationsClient<PreferencesGrpc.PreferencesClient>(NotificationsHop.PreferencesClientName);

        return services;
    }
}
