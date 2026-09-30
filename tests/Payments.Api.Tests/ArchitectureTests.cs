using System.Reflection;
using Payments.Domain.Intents;
using Payments.Infrastructure.Persistence;
using Payments.Migrator;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Payments.Api.Tests;

/// <summary>§4.2's composition-root rule, over the whole assembly with the root subtracted afterwards.</summary>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Payments.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>Program or its global-namespace generated helpers, the one exemption §4.2 grants.</summary>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Every banned package, not the Infrastructure namespace alone: DbContext and the like arrive transitively.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .ShouldNot().HaveDependencyOnAny(Forbidden)
            .GetResult();

        string[] leaked = [.. (result.FailingTypeNames ?? []).Where(name => !IsCompositionRoot(name))];

        leaked.ShouldBeEmpty($"leaked: {string.Join(", ", leaked)}");
    }

    [Fact]
    public void The_composition_root_is_the_only_thing_exempted()
    {
        string[] exempted =
        [
            .. typeof(Program).Assembly
                .GetTypes()
                .Select(type => type.FullName ?? type.Name)
                .Where(IsCompositionRoot)
        ];

        // Program's presence says the rule is looking at this host rather than at an empty list.
        exempted.ShouldContain("Program");

        // A ceiling, so the exemption cannot quietly grow.
        exempted.Length.ShouldBeLessThanOrEqualTo(
            4,
            "the exemption should cover Program and its own generated helpers, nothing more");
    }

    /// <summary>The five projects §4.1 gives a service, anchored one type each.</summary>
    /// <remarks>Emitted references, narrower than §4.2's table: an unused ProjectReference emits nothing.</remarks>
    private static readonly Assembly[] ServiceAssemblies =
    [
        typeof(PaymentIntent).Assembly,
        typeof(Payments.Application.DependencyInjection).Assembly,
        typeof(PaymentsDbContext).Assembly,
        typeof(MigratorHost).Assembly,
        typeof(Program).Assembly
    ];

    /// <summary>Whether a referenced assembly is this repository's own.</summary>
    private static bool IsFirstParty(AssemblyName reference) =>
        reference.GetPublicKeyToken() is null or [] && reference.Name != "Dapper";

    [Fact]
    public void No_project_in_this_service_references_another_service()
    {
        // §4.2's "must never reference another service's projects", and §4.3 from the other side: Common.* may
        // cross. An allow-list of prefixes, so it covers services that do not exist yet.
        string self = typeof(Program).Assembly.GetName().Name!.Split('.')[0];

        foreach (Assembly assembly in ServiceAssemblies)
        {
            string[] foreign =
            [
                .. assembly
                    .GetReferencedAssemblies()
                    .Where(IsFirstParty)
                    .Select(reference => reference.Name!)
                    .Where(name =>
                        !name.StartsWith("Common.", StringComparison.Ordinal) &&
                        !name.StartsWith($"{self}.", StringComparison.Ordinal))
                    .Order()
            ];

            foreign.ShouldBeEmpty(
                $"{assembly.GetName().Name} reaches across a service boundary: " +
                string.Join(", ", foreign));
        }
    }

    [Fact]
    public void Nothing_in_this_service_references_the_migrator()
    {
        // The Migrator is a leaf (§7.4), and the gate above subtracts this service's own prefix.
        string self = typeof(Program).Assembly.GetName().Name!.Split('.')[0];
        string migrator = $"{self}.Migrator";

        foreach (Assembly assembly in ServiceAssemblies)
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
        // §4.2's narrowest row, "anything it does not need to apply a migration", as an allow-list: no Application,
        // MassTransit, Redis, ASP.NET or Common.*.
        string[] allowed =
        [
            "Payments.Infrastructure",
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
