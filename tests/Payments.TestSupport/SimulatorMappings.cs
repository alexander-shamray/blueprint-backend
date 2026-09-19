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
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                continue;

            string mappings = Path.Combine(dir.FullName, "deploy", "compose", "psp-simulator", "mappings");
            if (!System.IO.Directory.Exists(mappings))
            {
                throw new InvalidOperationException(
                    $"Found the solution at {dir.FullName} but no simulator mappings at {mappings} (§14.1).");
            }

            return mappings;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the simulator's mappings cannot be found.");
    }
}
