using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Common.TestSupport;

/// <summary>A service's §7.4 job host, run in process for its exit code.</summary>
public static class MigratorRun
{
    /// <summary>Builds the host from the two §7.1 keys and runs it; a null argument leaves that key unset.</summary>
    public static async Task<int> RunAsync(
        string service,
        Func<string[], IHost> build,
        Func<IServiceProvider, CancellationToken, Task<int>> run,
        string? migratorConnectionString,
        string? runtimeConnectionString)
    {
        string[] args =
        [
            .. Setting($"ConnectionStrings:{service}Migrator", migratorConnectionString),
            .. Setting($"ConnectionStrings:{service}", runtimeConnectionString),
            .. PlaintextTestContainers(service)
        ];

        using IHost host = build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await run(scope.ServiceProvider, TestContext.Current.CancellationToken);

        static string[] Setting(string key, string? value) =>
            value is null ? [] : [$"--{key}={value}"];
    }

    /// <summary>
    /// ADR-079's opt-out for the job host, which runs as Production: the SQL container's certificate is self-signed.
    /// </summary>
    public static string[] PlaintextTestContainers(string service) =>
        [$"--Transport:Plaintext:0={service}Migrator", $"--Transport:Plaintext:1={service}"];
}
