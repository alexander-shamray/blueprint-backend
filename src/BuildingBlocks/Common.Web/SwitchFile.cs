using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Common.Web;

/// <summary>A JSON file that reloads and contributes only the keys its caller admits (ADR-187).</summary>
/// <remarks>
/// Layered last, a general overlay would let whoever writes the ConfigMap override any setting (ADR-187). A key not
/// admitted is left out on every load rather than the file refused, so it never takes a working switch with it.
/// </remarks>
public static class SwitchFile
{
    /// <summary>Optional and followed while the host runs, as ADR-113 and ADR-119 decide.</summary>
    public static IConfigurationBuilder AddSwitchFile(
        this IConfigurationBuilder configuration,
        string path,
        Func<string, bool> admits)
    {
        Source source = new(admits) { Path = path, Optional = true, ReloadOnChange = true };
        source.ResolveFileProvider();

        return configuration.Add(source);
    }

    private sealed class Source(Func<string, bool> admits) : JsonConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);

            return new Provider(this, admits);
        }
    }

    private sealed class Provider(JsonConfigurationSource source, Func<string, bool> admits)
        : JsonConfigurationProvider(source)
    {
        public override void Load(Stream stream)
        {
            base.Load(stream);
            Data = Data.Where(pair => admits(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
