using System.Reflection;
using Payments.Domain.Intents;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Payments.Application.Tests;

/// <summary>
/// The §4.2 gates for this layer. Green on an empty skeleton by design: a
/// rule introduced before the violations exist is a constraint, not a
/// backlog item.
/// </summary>
/// <remarks>
/// An allow-list over <c>GetReferencedAssemblies</c>: §4.2's row here says
/// what this project MAY reference, narrower than the table's word, since
/// an unused reference emits nothing until something names a type across it.
/// </remarks>
public class ArchitectureTests
{
    [Fact]
    public void Application_references_only_what_the_dependency_table_allows()
    {
        // §4.2's second row read as the allow-list it is: EF Core, ASP.NET,
        // Redis and MassTransit are excluded by not appearing, and so is
        // another service's assembly (§4.3). Dapper is the read side of
        // §6.5 — query handlers use it directly and never EF — and
        // System.Data.Common comes with it, since IDbConnectionFactory
        // hands back a DbConnection. Common.Domain is listed because this
        // gate reads assembly references, where the mapper's IDomainEvent
        // puts it here whether or not a csproj says so. A subset check, not
        // an equality, so adding an entry is a decision written down.
        string[] allowed =
        [
            "Payments.Domain",
            "Common.Application",
            "Common.Contracts",
            "Common.Domain",
            "Dapper",
            "FluentValidation",
            "FluentValidation.DependencyInjectionExtensions",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions",
            "System.Collections",
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

    // The two gates below are subsumed by the allow-list above and kept
    // deliberately. Each names the rule it enforces in its own failure
    // message, where the allow-list can only say that something is not on a
    // list — and the MassTransit one judges the Domain assembly as well, which
    // is a second assembly this class would otherwise say nothing about.
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
        // §9.3's must-not list. The saga may Send and Publish because its
        // receive endpoint carries a transactional outbox (ADR-032), which
        // writes those sends to the same DbContext and transaction as the
        // instance — a guarantee that exists on that one consume pipeline
        // and nowhere else. A handler that copies the saga's style gets a
        // dual write with no outbox behind it, and MassTransit's in-memory
        // buffer flushes after the consumer returns, after the repository
        // has already committed.
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(PaymentIntent).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
        }
    }
}
