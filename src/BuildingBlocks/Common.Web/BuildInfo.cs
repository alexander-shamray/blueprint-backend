using System.Reflection;

namespace Common.Web;

/// <summary>The version stamped onto the OpenTelemetry resource as <c>service.version</c> (§13.2).</summary>
public static class BuildInfo
{
    public static string Version { get; } = Normalise(Read());

    /// <summary>Strips the source-revision suffix; separate from <see cref="Version"/> so it can be tested.</summary>
    public static string Normalise(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "0.0.0";

        // A per-commit suffix would turn one series into thousands (§13.2).
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    // The entry assembly, because the version that matters is the host's, not this library's.
    private static string? Read() =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
}
