namespace Payments.TestSupport;

/// <summary>
/// The provider simulator's mapping files, where Compose mounts them from
/// (§14.1), so a test over an in-process server loads the same stubs the
/// local stack runs rather than a copy that could drift from them.
/// </summary>
public static class SimulatorMappings
{
    public static string Directory()
    {
        string root = RepositoryRoot();
        string mappings = Path.Combine(root, "deploy", "compose", "psp-simulator", "mappings");
        if (!System.IO.Directory.Exists(mappings))
        {
            throw new InvalidOperationException(
                $"Found the solution at {root} but no simulator mappings at {mappings} (§14.1).");
        }

        return mappings;
    }

    /// <summary>The directory holding <c>Platform.slnx</c>, walked up to from the test's own output.</summary>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the simulator's files cannot be found.");
    }
}
