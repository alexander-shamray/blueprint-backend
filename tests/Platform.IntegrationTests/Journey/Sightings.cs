namespace Platform.IntegrationTests.Journey;

/// <summary>A rare event a passing test saw, kept as a file so it outlives the run that saw it.</summary>
/// <remarks>
/// CI names the directory and uploads it (§15.1); a run without one writes under the temp path, so a local
/// sighting is a file too.
/// </remarks>
internal static class Sightings
{
    internal const string DirectoryVariable = "PLATFORM_SIGHTINGS_DIR";

    /// <summary>Writes <paramref name="text"/> to <c>name.txt</c> and returns the path.</summary>
    internal static string Record(string name, string text)
    {
        string? named = Environment.GetEnvironmentVariable(DirectoryVariable);
        string directory = string.IsNullOrWhiteSpace(named)
            ? Path.Combine(Path.GetTempPath(), "platform-sightings")
            : named;

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".txt");
        File.WriteAllText(path, text);

        return path;
    }
}
