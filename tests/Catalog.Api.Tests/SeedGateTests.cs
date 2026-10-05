using Catalog.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§14.3's gate at <see cref="MigratorHost"/>: a seeder only for a flag parsing true in Development.</summary>
public class SeedGateTests
{
    [Theory]
    [InlineData("true", "Development", true)]
    [InlineData("True", "Development", true)]
    [InlineData(null, "Development", false)]
    [InlineData("", "Development", false)]
    [InlineData("false", "Development", false)]
    [InlineData("1", "Development", false)]
    [InlineData("yes", "Development", false)]
    [InlineData("true", "Production", false)]
    [InlineData("true", "Staging", false)]
    [InlineData("true", "", false)]
    public void The_seeder_is_registered_only_for_a_true_flag_in_Development(
        string? flag,
        string environment,
        bool registered)
    {
        string[] seed = flag is null ? [] : [$"--Seed:Enabled={flag}"];

        // Never opened: building the host and resolving a context connects to nothing.
        string[] args =
        [
            $"--environment={environment}",
            "--ConnectionStrings:CatalogMigrator=Server=seed-gate.invalid;Database=Catalog",
            .. seed
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        (scope.ServiceProvider.GetService<CatalogSeeder>() is not null).ShouldBe(registered);

        // Either way, which only the runner's parameter default makes true when the gate is shut (§14.3).
        scope.ServiceProvider.GetRequiredService<MigrationRunner>().ShouldNotBeNull();
    }
}
