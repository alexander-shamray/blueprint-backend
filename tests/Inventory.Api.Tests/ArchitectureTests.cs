using System.Reflection;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Persistence;
using Inventory.Migrator;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Inventory.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and everything else in the host holds to Application and
/// Domain contracts, endpoints included. The rule was proved capable of
/// failing — against a deliberately added forbidden reference — in the
/// service this one was scaffolded from.
/// </summary>
/// <remarks>
/// <b>This gate has been wrong four times, and every one was the same mistake:
/// selecting less than it claimed.</b> A namespace
/// (<c>.ResideInNamespaceContaining(".Endpoints")</c>) stopped covering the
/// transport surface when <c>PricingService</c> arrived in <c>.Grpc</c>; a
/// namespace <i>pattern</i> moved the hole one namespace further out; excluding
/// compiler-generated types by name exempted **endpoint lambdas**, because a
/// closure is generated code; and filtering candidates through
/// <c>HaveName(...)</c> exempted them again, because that predicate selects
/// nothing for a nested async state machine and an empty selection reports
/// success.
/// <para>
/// So this version selects <b>nothing at all</b> and filters the
/// <i>result</i>. There is no candidate set to be narrow, no predicate to miss
/// a name, and no empty selection to pass vacuously — the assembly is judged
/// whole, and the composition root is subtracted from the failures afterwards,
/// where full names are available to subtract it by.
/// </para>
/// <para>
/// <b>A residual survives all four shapes, and it is the tool's rather than
/// the rule's.</b> NetArchTest does not analyse compiler-generated nested
/// types, so a forbidden reference used <i>only</i> inside an endpoint lambda
/// is invisible to it. Measured both ways, which is the only reason this is
/// stated rather than guessed: a <c>DbContextOptionsBuilder</c> in
/// an endpoint method's own body fails this gate and names that endpoint
/// class; the identical line inside one of its lambdas leaves it green —
/// including with no selector at all, which is what rules out a narrowing
/// predicate as the cause.
/// </para>
/// <para>
/// So the shape below is the strongest available and not a complete one. It
/// catches every reference written in a method body, which is where they are
/// written; the lambda case is named here rather than papered over, because a
/// gate believed to be total is worse than one whose gap is written down.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Inventory.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>
    /// Whether a failing type is the composition root or code the compiler
    /// emitted <i>for it</i> — the one exemption §4.2 grants.
    /// </summary>
    /// <remarks>
    /// Top-level statements put <c>Program</c> and its helpers
    /// (<c>&lt;PrivateImplementationDetails&gt;</c>, the anonymous delegate
    /// types its lambdas need) in the <b>global namespace</b>, so they carry no
    /// dot. Everything an endpoint generates is nested inside the endpoint
    /// class and keeps its namespace — which is exactly the distinction the
    /// name-prefix exclusion could not draw.
    /// </remarks>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach a
        // type transitively without any Inventory.Infrastructure dependency to
        // trip on.
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

        // The exemption is the whole of this gate's trust, so it is asserted
        // rather than assumed: it must cover the root and must not quietly
        // grow. `Program` being present is what says the rule is looking at
        // this host at all — a wrongly-anchored assembly reference would
        // produce an empty list here and a vacuously green rule above.
        exempted.ShouldContain("Program");

        // Bounds the exemption itself: it must cover Program and its own
        // compiler-generated helpers and nothing beyond that, or a wrongly
        // widened predicate could exempt a real violator alongside it.
        exempted.Length.ShouldBeLessThanOrEqualTo(
            4,
            "the exemption should cover Program and its own generated helpers, nothing more");
    }

    /// <summary>
    /// The five projects §4.1 gives a service, anchored one type each. This
    /// suite is the only one that can see all five: it references the Api,
    /// which carries Application, Domain and Infrastructure, and
    /// <c>Inventory.TestSupport</c>, which carries the Migrator.
    /// </summary>
    /// <remarks>
    /// <b>Both gates below read emitted references, which is narrower than the
    /// table's word, and a green run should be read knowing it.</b>
    /// <c>GetReferencedAssemblies</c> reads the <c>AssemblyRef</c> table, and
    /// the compiler writes an entry only for an assembly whose types the
    /// compiled code actually names. A forbidden <c>ProjectReference</c> — a
    /// foreign service's, or one the migrator's row excludes — that nothing
    /// <i>uses</i> emits nothing, so both gates go green on a project that
    /// declares one. Each fires the moment any code names a type across that
    /// edge, which makes them late rather than absent: the escape needs the
    /// reference to be both forbidden and entirely unused.
    /// <para>
    /// Closing it means reading the declared graph instead of the compiled
    /// one, which is a repo-wide build change with a silent-failure mode of
    /// its own — see §4.2, which states the reach and what closing it would
    /// cost. The limit belongs to the Domain and Application gates in the
    /// sibling suites as much as to these two, and predates the PR that wrote
    /// this comment.
    /// </para>
    /// </remarks>
    private static readonly Assembly[] ServiceAssemblies =
    [
        typeof(StockItem).Assembly,
        typeof(Inventory.Application.DependencyInjection).Assembly,
        typeof(InventoryDbContext).Assembly,
        typeof(MigratorHost).Assembly,
        typeof(Program).Assembly
    ];

    /// <summary>
    /// Whether a referenced assembly is one of this repository's own rather
    /// than a package.
    /// </summary>
    /// <remarks>Not a list of service names: the scaffold renames every
    /// casing of the template's name after applying its patches, so a list
    /// naming the template would reach the new service with it replaced
    /// rather than joined. Strong-naming stands in instead, on §4.2's
    /// argument about what a second unsigned package would cost.</remarks>
    private static bool IsFirstParty(AssemblyName reference) =>
        reference.GetPublicKeyToken() is null or [] && reference.Name != "Dapper";

    [Fact]
    public void No_project_in_this_service_references_another_service()
    {
        // §4.2's "must never reference: another service's projects" — the
        // Infrastructure, Migrator and Api rows say it, and until now nothing
        // said it in a test. The other two rows do not say it and do not need
        // to: their allow-lists cannot admit a foreign assembly at all, which
        // is why this gate judges all five and expects to be redundant on two. It is also §4.3 from the other side: exactly one assembly may
        // cross a service boundary, and Common.Contracts is a building block
        // rather than a service, so it is admitted by the Common prefix and
        // needs no exception of its own.
        //
        // Stated as an allow-list of prefixes rather than a deny-list of
        // service names, which is what makes it cover Inventory, Payments,
        // Shipping and Notifications before any of them exists.
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
        // §4.2's rows say what each project MAY reference, and no row names
        // the Migrator: Domain takes Common.Domain, Application takes its own
        // Domain and the Common pair, Infrastructure takes Domain and
        // Application, and the Api takes Application and Infrastructure. The
        // migrator is a leaf — a job host that resolves a DbContext and calls
        // Database.Migrate() (§7.4) — so it references and is not referenced.
        //
        // The cross-service gate above cannot say this, and is not meant to:
        // it subtracts everything under this service's own prefix, which is
        // exactly what makes it silent about an Api -> Migrator edge. That
        // edge is inside one service and still forbidden, so it takes a rule
        // of its own rather than a cleverer prefix. The two gates ask "whose
        // is it" and "which layer is it", and one predicate answering both
        // would answer neither legibly.
        //
        // The migrator is skipped as a subject rather than special-cased in
        // the predicate: an assembly does not reference itself, so including
        // it would pass vacuously and read as coverage.
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
        // §4.2's narrowest row, and the only one whose "must never" is a
        // sentence rather than a list: "anything it does not need to apply a
        // migration". A deny-list cannot enforce that — it can only ban what
        // somebody thought of — so this row gets the allow-list treatment the
        // Domain row gets, and for the same reason.
        //
        // What the absences are worth saying out loud: no Application, so the
        // migrator cannot dispatch; no MassTransit and no Redis, so §4.2's
        // "a migration job that can open a message broker is a migration job
        // with reasons to fail that have nothing to do with migrations" is a
        // build failure rather than a paragraph; no ASP.NET, because it is a
        // job host (§7.4) and not a second composition root; and no Common.*
        // at all, which is the strongest statement of the row — the migrator
        // resolves a DbContext and calls Database.Migrate(), and none of the
        // building blocks is on that path.
        string[] allowed =
        [
            "Inventory.Infrastructure",
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
