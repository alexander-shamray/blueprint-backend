using System.Reflection;
using Ordering.Domain.Orders;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Ordering.Application.Tests;

/// <summary>§4.2's gates for this layer.</summary>
/// <remarks>
/// <c>GetReferencedAssemblies</c> sees only references the compiled code names, so a forbidden but unused
/// reference passes (§4.2).
/// </remarks>
public class ArchitectureTests
{
    [Fact]
    public void Application_references_only_what_the_dependency_table_allows()
    {
        // §4.2's second row as an allow-list; System.Collections.Immutable is emitted with no source naming it.
        string[] allowed =
        [
            "Common.Application",
            "Common.Contracts",
            "Common.Domain",
            "Dapper",
            "FluentValidation",
            "FluentValidation.DependencyInjectionExtensions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Ordering.Domain",
            "System.Collections",
            "System.Collections.Immutable",
            "System.Data.Common",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Runtime"
        ];

        string[] unexpected =
        [
            .. typeof(DependencyInjection).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => !allowed.Contains(name))
                .Order()
        ];

        unexpected.ShouldBeEmpty($"not on §4.2's list: {string.Join(", ", unexpected)}");
    }

    // Also caught by the allow-list above, and kept because it names its rule in its own failure message.
    [Fact]
    public void Application_does_not_depend_on_ef_core()
    {
        TestResult result = Types
            .InAssembly(typeof(DependencyInjection).Assembly)
            .ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"leaked: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Application_and_domain_do_not_reference_masstransit()
    {
        // §9.3's must-not list, whose one exemption is a saga's receive endpoint and its outbox (ADR-032).
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Order).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
        }
    }
}
