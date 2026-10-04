namespace BffReplay;

/// <summary>The six connections a replay takes, all of them or none.</summary>
public sealed record ReplaySettings(string Bff, string Broker, IReadOnlyList<PublisherConnection> Publishers)
{
    /// <summary>The BFF's runtime key (§7.1), whose rows the reset deletes.</summary>
    public const string BffKey = "ConnectionStrings__Bff";

    /// <summary>The bus, under <c>bff-svc</c>, whose grant writes no contract exchange (ADR-036).</summary>
    public const string BrokerKey = "ConnectionStrings__RabbitMq";

    /// <summary>Every key a replay reads, in the order a refusal names them.</summary>
    public static IReadOnlyList<string> Keys => [BffKey, BrokerKey, .. Publisher.All.Select(p => p.Key)];

    /// <summary>Reads every key, and refuses with every missing one named rather than the first.</summary>
    public static ReplaySettings FromEnvironment(Func<string, string?> environment)
    {
        string[] missing = [.. Keys.Where(key => string.IsNullOrWhiteSpace(environment(key)))];
        if (missing.Length > 0)
            throw new ReplaySettingsException(missing);

        return new ReplaySettings(
            environment(BffKey)!,
            environment(BrokerKey)!,
            [.. Publisher.All.Select(p => new PublisherConnection(p, environment(p.Key)!))]);
    }
}
