using System.Diagnostics;
using System.Net;
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
        applied.Length.ShouldBe(2);
        applied[0].ShouldEndWith("_AddOrderProjection");
        applied[1].ShouldEndWith("_IndexUnattributedOrders");
    }

    [Fact]
    public async Task Ready_probe_answers_200_against_the_migrated_database()
    {
        using HttpClient client = fixture.Factory.CreateClient();

        // A poll, because the SQL check runs on each request and the first can race the container's first login.
        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            using HttpResponseMessage response =
                await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            status = response.StatusCode;

            if (status == HttpStatusCode.OK)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        status.ShouldBe(HttpStatusCode.OK, "the projection's database is up and migrated");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 reruns this on every deploy, so applying nothing has to succeed.
        string[] before = await fixture.AppliedMigrationsAsync();

        int exitCode = await BffServiceFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
        (await fixture.AppliedMigrationsAsync()).ShouldBe(before, "a second run applies nothing");
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
