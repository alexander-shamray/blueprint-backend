using System.Reflection;
using Shipping.Domain;
using Shipping.Infrastructure.Persistence;
using Shipping.Migrator;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Shipping.Worker.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and everything else in the host holds to Application and
/// Domain contracts.
/// </summary>
/// <remarks>The gate selects nothing and filters the result, because a
/// selector that narrows to no candidate reports success rather than failure.
/// The residual is the tool's: NetArchTest does not analyse
/// compiler-generated nested types, so a forbidden reference used only inside
/// an endpoint lambda is invisible to it.</remarks>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Shipping.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>
    /// Whether a failing type is the composition root or code the compiler
    /// emitted for it — the one exemption §4.2 grants.
    /// </summary>
    /// <remarks>Top-level statements put <c>Program</c> and its helpers
    /// (<c>&lt;PrivateImplementationDetails&gt;</c>, the anonymous delegate
    /// types its lambdas need) in the global namespace, so they carry no dot.
    /// Everything an endpoint generates is nested inside the endpoint class
    /// and keeps its namespace, which is what this predicate turns on.</remarks>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach a
        // type transitively without any Shipping.Infrastructure dependency to
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

        // The other half of this assertion belongs with the first endpoint:
        // that the gate is judging something. Until then Program is all
        // there is, and naming an adapter that does not exist is not an
        // assertion — see the service this one was scaffolded from.
        exempted.Length.ShouldBeLessThanOrEqualTo(
            4,
            "the exemption should cover Program and its own generated helpers, nothing more");
    }

    /// <summary>
    /// Every project §4.1 gives a service, anchored one type each: the Api
    /// reference carries Application, Domain and Infrastructure, and the
    /// <c>Shipping.TestSupport</c> one carries the Migrator.
    /// </summary>
    /// <remarks>Both gates below read emitted references: a forbidden
    /// <c>ProjectReference</c> nothing uses emits nothing, so they are late
    /// rather than absent. §4.2 states the reach and what closing it would
    /// cost, and the limit is the sibling suites' gates' too.</remarks>
    private static readonly Assembly[] ServiceAssemblies =
    [
        typeof(AssemblyMarker).Assembly,
        typeof(Shipping.Application.DependencyInjection).Assembly,
        typeof(ShippingDbContext).Assembly,
        typeof(MigratorHost).Assembly,
        typeof(Program).Assembly
    ];

    /// <summary>
    /// Whether a referenced assembly is one of this repository's own rather
    /// than a package.
    /// </summary>
    /// <remarks>Not a list of service names: the scaffold renames every
    /// casing of the template's name after applying its patches, so a list
    /// naming <c>Shipping</c> would reach the new service with it replaced
    /// rather than joined. Strong-naming stands in, since no project here is
    /// signed, and an unsigned package fails the gate loudly rather than
    /// opening a hole quietly.</remarks>
    private static bool IsFirstParty(AssemblyName reference) =>
        reference.GetPublicKeyToken() is null or [] && reference.Name != "Dapper";

    [Fact]
    public void No_project_in_this_service_references_another_service()
    {
        // §4.2's "must never reference: another service's projects" — the
        // Infrastructure, Migrator and Api rows say it. The remaining rows do
        // not, and do not need to: their allow-lists cannot admit a foreign
        // assembly at all, which is why this gate judges every assembly §4.1
        // gives a service and is redundant wherever an allow-list already
        // covers one. It is also §4.3 from the other side: exactly one
        // assembly may cross a service boundary, and Common.Contracts is a
        // building block rather than a service, so it is admitted by the
        // Common prefix and needs no exception of its own. Prefixes rather
        // than service names, so it covers a service that does not exist yet.
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
        // the Migrator: Domain takes Common.Domain, Application takes its
        // own Domain and the Common pair, Infrastructure takes Domain and
        // Application, and the Api takes Application and Infrastructure —
        // the migrator is a leaf (§7.4) that references and is not
        // referenced. The cross-service gate above stays silent about an
        // Api -> Migrator edge, since it subtracts everything under this
        // service's own prefix; skipped as a subject here rather than
        // special-cased, since an assembly does not reference itself.
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
        // migration". A deny-list cannot enforce that, so this row gets the
        // allow-list treatment the Domain row gets. No Application, so the
        // migrator cannot dispatch; no MassTransit and no Redis, so a
        // migration job with reasons to fail unrelated to migrations is a
        // build failure rather than a paragraph; no ASP.NET, because it is a
        // job host (§7.4), not a second composition root; and no Common.*
        // at all, since it resolves a DbContext and calls
        // Database.Migrate() with none of the building blocks on that path.
        string[] allowed =
        [
            "Shipping.Infrastructure",
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
