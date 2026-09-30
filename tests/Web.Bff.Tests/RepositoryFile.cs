namespace Web.Bff.Tests;

/// <summary>Locates a repository file by walking up from the test binary to <c>Platform.slnx</c>.</summary>
public static class RepositoryFile
{
    /// <summary>The shipped Keycloak realm (§14.1).</summary>
    public const string RealmExport = "deploy/compose/keycloak/realm-export.json";

    /// <summary>The BFF's own Compose unit (§14.1), not the index, which declares no environment.</summary>
    public const string ComposeFile = "deploy/compose/services/web-bff.yml";

    public static string Locate(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");
        }

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file fails here rather than as an empty realm that asserts nothing.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
