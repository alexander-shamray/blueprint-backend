using System.Reflection;
using Inventory.Domain.Stock;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Inventory.Application.Tests;

/// <summary>
/// The §4.2 gates for this layer. Green on an empty skeleton by design — "an
/// architecture rule introduced before the violations exist is a constraint",
/// and these have been observed failing against a deliberately added
/// forbidden reference.
/// </summary>
/// <remarks>
/// <b>Two shapes, because §4.2's table has two.</b> A row that says what a
/// project <i>may</i> reference gets an allow-list gate, and a row that says a
/// project may reference any package gets a named deny. This project's row is
/// the first kind, so the gate below is an allow-list over
/// <c>GetReferencedAssemblies</c> — the same instrument the Domain gate uses
/// one project down, and for the same reason: a blacklist only bans what
/// somebody thought to name.
/// <para>
/// <b>What that instrument sees is narrower than the table's word, and the
/// gap is worth knowing before trusting a green run.</b>
/// <c>GetReferencedAssemblies</c> reads the emitted <c>AssemblyRef</c> table,
/// and the compiler writes an entry only for an assembly whose types the
/// compiled code actually names. A forbidden <c>ProjectReference</c> or
/// <c>PackageReference</c> that nothing <i>uses</i> emits nothing, so this
/// gate goes green on a project that declares one. It fires the moment any
/// code names a type across that edge, which makes the gate late rather than
/// absent — the escape needs the reference to be both forbidden and entirely
/// unused.
/// </para>
/// <para>
/// Closing it means reading the declared graph instead of the compiled one,
/// which is a repo-wide build change with a silent-failure mode of its own —
/// see §4.2, which states the reach and what closing it would cost. The limit
/// belongs to the Domain gate one project down as much as to this one.
/// </para>
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
        // directly and never EF, which Inventory.Application.csproj states at
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
            "Inventory.Domain",
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
        // §9.3's must-not list. The saga may Send and Publish because its receive
        // endpoint carries a transactional outbox (ADR-032), which writes those
        // sends to the same DbContext and the same transaction as the instance —
        // a guarantee that exists on that one consume pipeline and nowhere else.
        // A handler that copies the saga's style gets a dual write with no outbox
        // behind it, and it works in every test where the broker is up.
        //
        // MassTransit's in-memory outbox does not close that gap on its own:
        // the buffer flushes AFTER the consumer returns, which is after the
        // repository has committed, so a handler relying on it alone can
        // still publish against a write that never lands.
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(StockItem).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
        }
    }
}
