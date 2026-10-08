using Common.Infrastructure.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Catalog.Infrastructure.Persistence;

/// <summary>ADR-079's check for the migrator, which §4.2 keeps from referencing Common.Infrastructure itself.</summary>
public static class MigratorTransport
{
    public static string? Refusal(IConfiguration configuration, IHostEnvironment environment) =>
        TransportSecurity.Refusal(configuration, environment);
}
