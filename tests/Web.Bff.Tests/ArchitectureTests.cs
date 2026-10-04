using System.Reflection;
using Shouldly;
using Web.Bff.Migrator;
using Web.Bff.Persistence;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§4.2's <c>*.Migrator</c> rules over the BFF's three projects (§4.1), which no other suite does.</summary>
public class ArchitectureTests
{
    /// <summary>The projects §4.1 gives the BFF, anchored one type each.</summary>
    private static readonly Assembly[] BffAssemblies =
    [
        typeof(Program).Assembly,
        typeof(BffDbContext).Assembly,
        typeof(MigratorHost).Assembly
    ];

    [Fact]
    public void Nothing_in_the_BFF_references_the_migrator()
    {
        // The Migrator is a leaf (§7.4): §4.2 gives no row a reference to it.
        string migrator = typeof(MigratorHost).Assembly.GetName().Name!;

        foreach (Assembly assembly in BffAssemblies)
        {
            if (assembly.GetName().Name == migrator)
                continue;

            string[] referenced = [.. assembly.GetReferencedAssemblies().Select(name => name.Name!)];

            referenced.ShouldNotContain(
                migrator,
                $"{assembly.GetName().Name} references the migration job host, which no §4.2 row permits");
        }
    }

    [Fact]
    public void The_migrator_references_only_what_applying_a_migration_needs()
    {
        // §4.2's narrowest row, "anything it does not need to apply a migration", as an allow-list: no MassTransit,
        // Redis, ASP.NET or Common.*. Web.Bff.Persistence stands where a service's Infrastructure does.
        string[] allowed =
        [
            "Web.Bff.Persistence",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Relational",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.Configuration.Abstractions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Hosting",
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions",
            "System.ComponentModel",
            "System.Linq",
            "System.Runtime"
        ];

        string[] unexpected =
        [
            .. typeof(MigratorHost).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => !allowed.Contains(name))
                .Order()
        ];

        unexpected.ShouldBeEmpty(
            $"the migrator does not need: {string.Join(", ", unexpected)}");
    }
}
