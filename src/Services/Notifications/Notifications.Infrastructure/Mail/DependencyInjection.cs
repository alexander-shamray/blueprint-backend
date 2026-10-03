using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Mail;

/// <summary>The relay's registration, beside the layer's, as its environment rule needs the host (ADR-055).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMailChannel(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Development's alone, refused before the host is built, as AddCarrierGateway refuses plain HTTP (§15.4).
        if (!environment.IsDevelopment())
            RefusePlainOrAnonymous(configuration.GetSection(MailOptions.SectionName).Get<MailOptions>());

        // Validated at start beside its consumer (§15.4): IOptions<T> always resolves, so a forgotten bind is silent.
        services
            .AddOptions<MailOptions>()
            .BindConfiguration(MailOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MailOptions>, MailOptionsValidator>();

        services.AddSingleton<MailMetrics>();

        // A singleton, so the breaker inside it is the host's and not a scope's.
        services.AddSingleton<MailPipeline>();

        return services;
    }

    // No message echoes a configured value: a failed start is logged, and the password sits beside the rest.
    private static void RefusePlainOrAnonymous(MailOptions? relay)
    {
        if (relay?.Security == MailSecurity.None)
        {
            throw new InvalidOperationException(
                $"{MailOptions.SecurityKey} is None outside Development; " +
                "the credential and every message would travel in the clear.");
        }

        if (string.IsNullOrWhiteSpace(relay?.UserName) || string.IsNullOrWhiteSpace(relay?.Password))
        {
            throw new InvalidOperationException(
                $"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are required outside Development; " +
                "a host does not submit to a relay anonymously.");
        }
    }
}
