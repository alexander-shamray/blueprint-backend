using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Common.Infrastructure.Transport;

/// <summary>ADR-079's check, registered by every host that opens an infrastructure connection.</summary>
public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>Refuses to start, before any hosted service connects, on a connection ADR-079 refuses.</summary>
        /// <remarks>Idempotent, so each registration that opens a connection may ask for it (ADR-079).</remarks>
        public IServiceCollection AddTransportSecurity()
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<TransportSecurityCheck>, TransportSecurityValidator>());
            services.AddOptions<TransportSecurityCheck>().ValidateOnStart();
            return services;
        }
    }
}

/// <summary>Carries nothing: the options type <see cref="TransportSecurityValidator"/> validates on start.</summary>
public sealed class TransportSecurityCheck;

internal sealed class TransportSecurityValidator(IConfiguration configuration, IHostEnvironment environment)
    : IValidateOptions<TransportSecurityCheck>
{
    public ValidateOptionsResult Validate(string? name, TransportSecurityCheck options) =>
        TransportSecurity.Refusal(configuration, environment) is { } refusal
            ? ValidateOptionsResult.Fail(refusal)
            : ValidateOptionsResult.Success;
}
