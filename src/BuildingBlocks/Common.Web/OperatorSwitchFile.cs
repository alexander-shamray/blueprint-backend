using Microsoft.Extensions.Configuration;

namespace Common.Web;

/// <summary>The one configuration source that reloads, because an environment variable cannot (ADR-113).</summary>
public static class OperatorSwitchFile
{
    /// <summary>Where a mounted ConfigMap puts the file; absent everywhere else, so nothing is off.</summary>
    public const string DefaultPath = "/etc/commerce/switches/switches.json";

    private const string Off = "Switches:Off";

    /// <summary>A mounted ConfigMap's update reaches the file; no file is none off, and no key but
    /// <c>Switches:Off</c> and its items is read from it (ADR-187).</summary>
    public static IConfigurationBuilder AddOperatorSwitchFile(
        this IConfigurationBuilder configuration,
        string path = DefaultPath) =>
        configuration.AddSwitchFile(path, Admits);

    /// <summary>The scalar is admitted too, so ADR-147's refusal still sees one.</summary>
    private static bool Admits(string key) =>
        key.Equals(Off, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(Off + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);
}
