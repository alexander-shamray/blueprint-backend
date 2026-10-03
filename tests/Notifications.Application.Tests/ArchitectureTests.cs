using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Notifications.Application.Tests;

/// <summary>§4.2's gates for this layer.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Application_references_only_what_the_dependency_table_allows()
    {
        // §4.2's second row as an allow-list, with no Domain project because §4.1 gives none: Dapper is §6.5's
        // read side and brings System.Data.Common, and System.Text.Json is the stored parameters' format.
        string[] allowed =
        [
            "Common.Application",
            "Common.Contracts",
            "Common.Domain",
            "Dapper",
            "FluentValidation",
            "FluentValidation.DependencyInjectionExtensions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "System.Collections",
            "System.Data.Common",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Runtime",
            "System.Text.Json"
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
    public void Application_does_not_reference_masstransit()
    {
        // §9.3's must-not list, over the one layer of the two it names that §4.1 gives this service.
        Types
            .InAssembly(typeof(DependencyInjection).Assembly)
            .ShouldNot().HaveDependencyOn("MassTransit")
            .GetResult().IsSuccessful.ShouldBeTrue();
    }
}
