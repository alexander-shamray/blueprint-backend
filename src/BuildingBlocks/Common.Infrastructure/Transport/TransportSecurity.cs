using System.Data.Common;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Common.Infrastructure.Transport;

/// <summary>ADR-079: outside Development, an infrastructure hop is encrypted unless a deployer says not.</summary>
public static class TransportSecurity
{
    /// <summary>The connection-string names a deployer accepts as plaintext, each one deliberately (ADR-079).</summary>
    public const string PlaintextKey = "Transport:Plaintext";

    public const string BrokerConnection = "RabbitMq";

    /// <summary>The first connection outside the rule, by name only, since a value carries its credential.</summary>
    public static string? Refusal(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
            return null;

        HashSet<string> plaintext = new(
            configuration.GetSection(PlaintextKey).GetChildren().Select(c => c.Value ?? ""),
            StringComparer.OrdinalIgnoreCase);

        foreach (IConfigurationSection connection in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(connection.Value) || plaintext.Contains(connection.Key))
                continue;

            // Configuration keys match ignoring case, as GetConnectionString reads them, so the classes must too.
            string? why = Is(connection.Key, BrokerConnection) ? Broker(connection.Value)
                : Is(connection.Key, RedisConnections.Cache) || Is(connection.Key, RedisConnections.Coordination)
                    ? Redis(connection.Value)
                    : SqlServer(connection.Value);
            if (why is not null)
            {
                return $"ConnectionStrings:{connection.Key} {why} in the {environment.EnvironmentName} environment " +
                       $"(ADR-079). Encrypt the hop, or name it under {PlaintextKey} to accept it as plaintext.";
            }
        }

        return null;
    }

    private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static string? Broker(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == "amqps"
            ? null
            : "is not an amqps:// address";

    private static string? Redis(string value)
    {
        try
        {
            return ConfigurationOptions.Parse(value).Ssl ? null : "does not set ssl=true";
        }
        catch (ArgumentException)
        {
            return "is not a Redis configuration this host can read";
        }
    }

    // SqlClient encrypts and validates by default, so only an explicit downgrade is refused.
    private static string? SqlServer(string value)
    {
        DbConnectionStringBuilder builder = new();
        try
        {
            builder.ConnectionString = value;
        }
        catch (ArgumentException)
        {
            return "is not a SQL Server connection string this host can read";
        }

        if (Setting(builder, "Encrypt") is { } encrypt && encrypt is "false" or "no" or "optional")
            return "sets Encrypt to " + encrypt;
        // SqlClient folds the two spellings into one key and keeps the last, so either one set is a downgrade.
        if (Setting(builder, "TrustServerCertificate") is "true" or "yes" ||
            Setting(builder, "Trust Server Certificate") is "true" or "yes")
            return "trusts any server certificate";
        return null;
    }

    private static string? Setting(DbConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out object? value) ? value?.ToString()?.Trim().ToLowerInvariant() : null;
}
