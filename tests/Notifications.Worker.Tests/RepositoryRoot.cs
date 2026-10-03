namespace Notifications.Worker.Tests;

/// <summary>The directory holding <c>Platform.slnx</c>, walked up to from the test's own output.</summary>
internal static class RepositoryRoot
{
    public static string Locate()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }
}
