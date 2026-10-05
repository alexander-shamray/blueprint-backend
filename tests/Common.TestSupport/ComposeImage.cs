using System.Text.RegularExpressions;

namespace Common.TestSupport;

/// <summary>§14.1's image for a baseline service, read rather than copied so a suite and a stack agree.</summary>
public static partial class ComposeImage
{
    public static string Of(string service) =>
        Of(service, File.ReadAllLines(Path.Combine(RepositoryRoot(), "deploy", "compose", "infrastructure.yml")));

    /// <summary>The <c>image:</c> among the keys indented under <paramref name="service"/>, in Compose's layout.</summary>
    public static string Of(string service, IReadOnlyList<string> lines)
    {
        bool under = false;
        foreach (string line in lines)
        {
            if (line.TrimEnd() == $"  {service}:")
            {
                under = true;
                continue;
            }

            if (!under)
                continue;

            if (line.Trim().Length > 0 && !line.StartsWith("    ", StringComparison.Ordinal))
                break;

            Match image = Image().Match(line);
            if (image.Success)
                return image.Groups["image"].Value;
        }

        throw new InvalidOperationException($"The Compose baseline names no image for '{service}' (§14.1).");
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"^ {4}image:\s*(?<image>\S+)\s*$")]
    private static partial Regex Image();
}
