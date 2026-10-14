using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Notifications.Api.Tests;

/// <summary>§4.2's composition-root rule over the second host, which the worker's suite cannot load.</summary>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Notifications.Infrastructure",
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
        exempted.Length.ShouldBeLessThanOrEqualTo(
            4,
            "the exemption should cover Program and its own generated helpers, nothing more");
    }

    /// <summary>The service's projects this host may emit a reference to (ADR-118).</summary>
    private static readonly string[] OwnProjects = ["Notifications.Application", "Notifications.Infrastructure"];

    /// <summary>§4.1's building blocks by name, because Common.TestSupport is named like one and is not.</summary>
    private static readonly string[] BuildingBlocks =
        ["Common.Application", "Common.Contracts", "Common.Domain", "Common.Infrastructure", "Common.Web"];

    /// <summary>The first-party references outside the allow-lists above.</summary>
    private static string[] Foreign(IEnumerable<AssemblyName> references) =>
    [
        .. references
            .Where(reference => reference.GetPublicKeyToken() is null or [])
            .Select(reference => reference.Name!)
            .Where(name => !BuildingBlocks.Contains(name) && !OwnProjects.Contains(name))
            .Order()
    ];

    [Fact]
    public void The_host_references_neither_the_other_host_nor_the_migrator_nor_another_service()
    {
        // The worker and the migrator are each a process of their own (§7.4, ADR-118), so neither is a library here.
        string[] foreign = Foreign(typeof(Program).Assembly.GetReferencedAssemblies());

        foreign.ShouldBeEmpty($"the API host reaches across a boundary: {string.Join(", ", foreign)}");
    }

    [Fact]
    public void The_gate_refuses_the_worker_the_migrator_and_a_test_library()
    {
        AssemblyName[] references =
        [
            new("Notifications.Infrastructure"), new("Notifications.Worker"), new("Notifications.Migrator"),
            new("Notifications.TestSupport"), new("Common.Web")
        ];

        Foreign(references).ShouldBe(["Notifications.Migrator", "Notifications.TestSupport", "Notifications.Worker"]);
    }
}
