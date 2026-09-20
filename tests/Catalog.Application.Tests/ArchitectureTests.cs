using System.Reflection;
using Catalog.Domain.Products;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Catalog.Application.Tests;

/// <summary>
/// The §4.2 gates for this layer. This project's row says what it may
/// reference, so the gate below is an allow-list rather than a named deny.
/// </summary>
/// <remarks>
/// <c>GetReferencedAssemblies</c> reads the emitted <c>AssemblyRef</c> table,
/// so a forbidden reference no code names emits nothing and goes green — the
/// gate is late rather than absent, here and at the Domain gate one project
/// down. §4.2 states the reach and what closing it would cost.
/// </remarks>
public class ArchitectureTests
{
    [Fact]
    public void Application_references_only_what_the_dependency_table_allows()
    {
        // §4.2's second row read as the allow-list it is. What this catches
        // that a deny-list cannot: EF Core, ASP.NET, Redis and MassTransit are
        // all excluded by not appearing, and so is another service's assembly,
        // which §4.3 forbids and which no deny-list here would have thought to
        // mention.
        //
        // Each entry earns its line, and the two that surprise a reader are
        // deliberate. Dapper is the read side of §6.5 — query handlers use it
        // directly and never EF, which Catalog.Application.csproj states at
        // the reference itself — and System.Data.Common comes with it, because
        // §6.5's IDbConnectionFactory hands back a DbConnection.
        //
        // Common.Domain is the third: §4.2's row names this service's Domain
        // and not the building block underneath it, because a service's Domain
        // cannot exist without Common.Domain (§4.2's first row) and arrives
        // carrying it. The table is about project references, where the line
        // is genuinely absent; this gate is about assembly references, where
        // the mapper's IDomainEvent puts it here whether or not a csproj says
        // so.
        //
        // The list is a subset check and not an equality: an entry for
        // something no longer referenced is a pre-authorised hole rather than
        // a failure, which is the same trade the Domain gate takes. What both
        // buy is that ADDING one is a decision somebody has to write down.
        string[] allowed =
        [
            "Catalog.Domain",
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
        // instance — a guarantee that exists on that one consume pipeline and
        // nowhere else. A handler that copies the saga's style gets a dual
        // write with no outbox behind it, and it works in every test where the
        // broker is up. MassTransit's in-memory outbox does not close that gap
        // on its own: the buffer flushes after the consumer returns, which is
        // after the repository has committed.
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Product).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
        }
    }
}
