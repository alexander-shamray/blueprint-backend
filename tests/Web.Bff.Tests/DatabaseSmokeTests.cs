using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The migrator against a real engine (ADR-010), and the schema it leaves (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class DatabaseSmokeTests(BffServiceFixture fixture)
{
    [Fact]
    public async Task Migrator_exits_zero_and_creates_the_schema()
    {
        // The fixture ran the real §7.4 job against an empty server, so this is its own outcome.
        fixture.FirstRunExitCode.ShouldBe(0);

        int schema = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'bff'");
        schema.ShouldBe(1);

        // Named, since a count passes on a different migration of the same length.
        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(1);
        applied[0].ShouldEndWith("_AddOrderProjection");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 reruns this on every deploy, so applying nothing has to succeed.
        int exitCode = await BffServiceFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Migrator_fails_when_only_the_runtime_connection_string_is_set()
    {
        // §7.1's split is a boundary only while the migrator reads its own key.
        int exitCode = await BffServiceFixture.RunMigratorAsync(
            migratorConnectionString: null,
            runtimeConnectionString: fixture.ConnectionString);

        exitCode.ShouldBe(1);
    }
}
