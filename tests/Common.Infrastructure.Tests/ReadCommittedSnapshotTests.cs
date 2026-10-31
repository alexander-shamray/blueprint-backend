using Common.Infrastructure.Migration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§7.1's isolation, read from real databases: made by hand, switched on, and made by the provider.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ReadCommittedSnapshotTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private async Task<string?> RefusalFor(string database)
    {
        await using SqlConnection connection = new(sql.ConnectionStringTo(database));
        return await ReadCommittedSnapshot.RefusalAsync(connection, TestContext.Current.CancellationToken);
    }

    private async Task OnMasterAsync(string statement)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SqlConnection master = new(sql.ConnectionStringTo("master"));
        await master.OpenAsync(ct);
        await using SqlCommand command = new(statement, master);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task A_database_an_operator_created_without_it_is_refused()
    {
        // A plain CREATE DATABASE leaves it off, which is what a database made before the first deploy holds.
        await OnMasterAsync("CREATE DATABASE [LockingReads];");

        string? refusal = await RefusalFor("LockingReads");

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("SET READ_COMMITTED_SNAPSHOT ON");
    }

    [Fact]
    public async Task A_database_switched_on_by_hand_is_accepted()
    {
        await OnMasterAsync(
            "CREATE DATABASE [SwitchedOn]; ALTER DATABASE [SwitchedOn] SET READ_COMMITTED_SNAPSHOT ON;");

        (await RefusalFor("SwitchedOn")).ShouldBeNull();
    }

    [Fact]
    public async Task A_database_the_provider_creates_runs_it()
    {
        // The subject is EF Core's CREATE DATABASE, which a migrator meeting no database runs: a provider bump that
        // stops setting it fails here rather than leaving each new database without row versioning unremarked.
        DbContextOptions options = new DbContextOptionsBuilder()
            .UseSqlServer(sql.ConnectionStringTo("ProviderCreated"))
            .Options;
        await using (DbContext db = new(options))
        {
            (await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        (await RefusalFor("ProviderCreated")).ShouldBeNull();
    }
}
