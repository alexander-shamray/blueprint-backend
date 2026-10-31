using Common.TestSupport;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>One real SQL Server on §14.1's image, for a building block's checks of a database (§12.4).</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = TestcontainersWarmUp.Build(() =>
        new MsSqlBuilder()
            .WithImage(ComposeImage.Of("sql"))
            .Build());

    /// <summary>A connection string to <paramref name="database"/>, which nothing here creates.</summary>
    public string ConnectionStringTo(string database) =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync() =>
        await DaemonRetry.StartAsync(_sql, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _sql.DisposeAsync();
}
