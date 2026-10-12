namespace Platform.IntegrationTests.Journey;

/// <summary>A rare event a passing test saw, kept as a file so it outlives the run that saw it.</summary>
/// <remarks>
/// CI names the directory and uploads it (§15.1); a run without one writes under the temp path, so a local
/// sighting is a file too.
/// </remarks>
internal static class Sightings
{
    internal const string DirectoryVariable = "PLATFORM_SIGHTINGS_DIR";

    /// <summary>The directory a named value, or the temp path when none is named.</summary>
    internal static string Resolve(string? named) =>
        string.IsNullOrWhiteSpace(named) ? Path.Combine(Path.GetTempPath(), "platform-sightings") : named;

    /// <summary>Writes the text to <c>name.txt</c> and returns the path; a test passes <paramref name="directory"/>.</summary>
    internal static string Record(string name, string text, string? directory = null)
    {
        string target = directory ?? Resolve(Environment.GetEnvironmentVariable(DirectoryVariable));

        Directory.CreateDirectory(target);
        string path = Path.Combine(target, name + ".txt");
        File.WriteAllText(path, text);

        return path;
    }
}
