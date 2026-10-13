using Microsoft.Extensions.Configuration;

namespace Common.Web;

/// <summary>The one configuration source that reloads, because an environment variable cannot (ADR-112).</summary>
public static class OperatorSwitchFile
{
    /// <summary>Where a mounted ConfigMap puts the file; absent everywhere else, so nothing is off.</summary>
    public const string DefaultPath = "/etc/commerce/switches/switches.json";

    /// <summary>A mounted ConfigMap's update reaches the file; no file is none off.</summary>
    public static IConfigurationBuilder AddOperatorSwitchFile(
        this IConfigurationBuilder configuration,
        string path = DefaultPath) =>
        configuration.AddJsonFile(path, optional: true, reloadOnChange: true);
}
