using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-185's check, held to every migrator under src/ rather than to the ones that exist today.</summary>
public sealed partial class MigratorIsolationTests
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

    [Theory]
    [InlineData("// if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "//     throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("/* if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal); */\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("await MigratorCommand.IsolationRefusalAsync(db, ct);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("var open = \"/*\"; await db.Database.MigrateAsync(ct); var close = \"*/\";\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("var sql = \"\"\"\n/*\n\"\"\"; await db.Database.MigrateAsync(ct); var tail = \"\"\"\n*/\n\"\"\";\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("var url = \"https://example.test\"; await db.Database.MigrateAsync(ct);\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("_ = db.Database.MigrateAsync(ct);\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("await db.Database.MigrateAsync(ct);\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);")]
    public void A_check_that_is_commented_out_discarded_or_late_does_not_count(string source) =>
        GuardsFirst(source).ShouldBeFalse();

    [Theory]
    [InlineData("var url = \"https://example.test\";\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("// .MigrateAsync( runs below\n"
        + "if (await MigratorCommand.IsolationRefusalAsync(db, ct) is { } refusal)\n"
        + "    throw new InvalidOperationException(refusal);\nawait db.Database.MigrateAsync(ct);")]
    [InlineData("if (await MigratorCommand.IsolationRefusalAsync(db, cancellationToken: ct).ConfigureAwait(false) "
        + "is { } refusal)\n{\n    throw new InvalidOperationException(refusal);\n}\ndb.Database.Migrate();")]
    public void A_guarded_check_ahead_of_the_migration_counts_whatever_a_comment_names(string source) =>
        GuardsFirst(source).ShouldBeTrue();

    // Before, so a refused database is left as the operator made it, with no migration applied to it.
    private static bool ChecksFirst(string runner) => File.Exists(runner) && GuardsFirst(File.ReadAllText(runner));

    // Read without comments, so neither call counts for being named, and only the form that throws on a refusal
    // counts as the check.
    private static bool GuardsFirst(string source)
    {
        string code = Comment().Replace(source, found => found.Groups["literal"].Success ? found.Value : string.Empty);
        Match check = GuardedCheck().Match(code);
        Match migrate = Migration().Match(code);
        return check.Success && migrate.Success && migrate.Index > check.Index;
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

    // A literal, raw, verbatim, regular or a character, is matched first and kept, so a `//` or `/*` inside one strips
    // no code; an interpolation hole's own literals are not told apart.
    [GeneratedRegex(
        @"(?<literal>(?<raw>""{3,}).*?\k<raw>|(?:@\$?|\$@)""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""|"
            + @"'(?:[^'\\\r\n]|\\.)+')|//[^\r\n]*|/\*.*?\*/",
        RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(
        @"^\s*if \(await MigratorCommand\.IsolationRefusalAsync\([^()]*\)(?:\.ConfigureAwait\(false\))? "
            + @"is \{ \} (?<refusal>\w+)\)\s*\{?\s*throw new InvalidOperationException\(\k<refusal>\);",
        RegexOptions.Multiline)]
    private static partial Regex GuardedCheck();

    // Any spelling of a migration counts, so one ahead of the check is seen however it is written.
    [GeneratedRegex(@"\.Migrate(?:Async)?\(")]
    private static partial Regex Migration();
}
