using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-185's check, held to every migrator under src/ rather than to the ones that exist today.</summary>
public sealed class MigratorIsolationTests
{
    [Fact]
    public void Every_migrator_reads_the_isolation_before_it_migrates()
    {
        string root = RepositoryRoot();
        string[] runners =
        [
            .. Directory.GetDirectories(Path.Combine(root, "src"), "*.Migrator", SearchOption.AllDirectories)
                .Select(project => Path.Combine(project, "MigrationRunner.cs"))
        ];

        runners.ShouldContain(r => r.Contains("Web.Bff.Migrator"), "a scan missing a migrator proves nothing of it");
        runners.Where(r => !ChecksFirst(r)).Select(r => Path.GetRelativePath(root, r)).ShouldBeEmpty();
    }

    // Before, so a refused database is left as the operator made it, with no migration applied to it.
    private static bool ChecksFirst(string runner)
    {
        if (!File.Exists(runner))
            return false;

        string source = File.ReadAllText(runner);
        int check = source.IndexOf("MigratorCommand.IsolationRefusalAsync(", StringComparison.Ordinal);
        int migrate = source.IndexOf(".MigrateAsync(", StringComparison.Ordinal);
        return check >= 0 && migrate > check;
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
}
