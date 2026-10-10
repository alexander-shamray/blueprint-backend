using System.Globalization;

namespace Platform.IntegrationTests.Journey;

/// <summary>Every column of every table of every database the platform runs, searched for a person (§11.7).</summary>
/// <remarks>
/// Text, not structure: the scan knows no table or database, so a store added later is searched without a line
/// here (§11.7). A column is read as text however it is typed, so a key, an address and a payload share one needle.
/// </remarks>
internal static class PersonalDataScan
{
    /// <summary>Every database on the server but the system's, which is one per service and the BFF's (§7.1).</summary>
    public static Task<IReadOnlyList<string>> DatabasesAsync(this JourneyWorld world) =>
        world.ColumnAsync("master", "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name");

    /// <summary>Types a text comparison cannot be asked of, and which hold no payload a person is written in.</summary>
    private const string Unreadable = "'timestamp','image','geography','geometry','hierarchyid','sql_variant'";

    /// <summary>The tables, as <c>Database.schema.table</c>, holding any of <paramref name="needles"/> in any column.</summary>
    public static async Task<IReadOnlySet<string>> HoldingAsync(this JourneyWorld world, params string[] needles)
    {
        SortedSet<string> holding = [];

        foreach (string database in await world.DatabasesAsync())
        {
            IReadOnlyList<string> tables = await world.ColumnAsync(
                database,
                "SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name) FROM sys.tables t " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0");

            foreach (string table in tables)
            {
                IReadOnlyList<string> columns = await world.ColumnAsync(
                    database,
                    "SELECT QUOTENAME(name) FROM sys.columns WHERE object_id = OBJECT_ID(@p0) " +
                    $"AND TYPE_NAME(user_type_id) NOT IN ({Unreadable})",
                    table);

                if (columns.Count == 0)
                    continue;

                string matches = string.Join(
                    " OR ",
                    columns.SelectMany(column => needles.Select((_, i) =>
                        $"TRY_CAST({column} AS nvarchar(max)) LIKE @p{i.ToString(CultureInfo.InvariantCulture)}")));

                int hits = await world.ScalarAsync<int>(
                    database,
                    $"SELECT COUNT(*) FROM {table} WHERE {matches}",
                    [.. needles.Select(needle => (object)$"%{needle}%")]);

                if (hits > 0)
                    holding.Add($"{database}.{table.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal)}");
            }
        }

        return holding;
    }
}
