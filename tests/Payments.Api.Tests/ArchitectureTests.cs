using System.Reflection;
using Payments.Domain;
using Payments.Infrastructure.Persistence;
using Payments.Migrator;
using NetArchTest.Rules;
using Shouldly;
using Xunit;
using TestResult = NetArchTest.Rules.TestResult;

namespace Payments.Api.Tests;

/// <summary>
/// §4.2's composition-root rule: only <c>Program.cs</c> may reference
/// Infrastructure, and everything else in the host holds to Application and
/// Domain contracts, endpoints included. The assembly is judged whole and
/// the root subtracted from the failures afterwards, rather than selected
/// out beforehand, because a predicate narrow enough to catch only the root
/// is also narrow enough to miss a forbidden reference elsewhere.
/// NetArchTest still cannot see inside a compiler-generated endpoint
/// lambda, so a reference used only there stays invisible to it.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Payments.Infrastructure",
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
    /// types its lambdas need) in the global namespace, so they carry no
    /// dot; an endpoint's own generated code stays nested in its namespace.
    /// </remarks>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Not the service's Infrastructure namespace alone: §4.2's rule is
        // "Application and Domain contracts only", and the concrete types it
        // bans — DbContext, IPublishEndpoint, IConnectionMultiplexer — reach a
        // type transitively without any Payments.Infrastructure dependency to
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
    /// The five projects §4.1 gives a service, anchored one type each:
    /// Domain, Application, Infrastructure, Migrator and Api.
    /// </summary>
    /// <remarks>
    /// Both gates read emitted references (<c>GetReferencedAssemblies</c>),
    /// narrower than the table's word: an unused <c>ProjectReference</c>
    /// emits nothing and passes until something names a type across it (§4.2).
    /// </remarks>
    private static readonly Assembly[] ServiceAssemblies =
    [
        typeof(AssemblyMarker).Assembly,
        typeof(Payments.Application.DependencyInjection).Assembly,
        typeof(PaymentsDbContext).Assembly,
        typeof(MigratorHost).Assembly,
        typeof(Program).Assembly
    ];

    /// <summary>
    /// Whether a referenced assembly is this repository's own, not a package.
    /// </summary>
    /// <remarks>
    /// Not a list of service names: the scaffold renames every casing of
    /// the template's name, so a list naming <c>Payments</c> would reach
    /// the new service with the name replaced, not joined. Measured
    /// instead: every pinned package is strong-named and none of this
    /// repository's own projects is.
    /// </remarks>
    private static bool IsFirstParty(AssemblyName reference) =>
        reference.GetPublicKeyToken() is null or [] && reference.Name != "Dapper";

    [Fact]
    public void No_project_in_this_service_references_another_service()
    {
        // §4.2's "must never reference: another service's projects" — the
        // Infrastructure, Migrator and Api rows say it, and the other two
        // rows do not need to: their allow-lists cannot admit a foreign
        // assembly at all. It is also §4.3 from the other side: exactly one
        // assembly may cross a service boundary, and Common.Contracts is a
        // building block rather than a service, admitted by the Common
        // prefix. Stated as an allow-list of prefixes rather than a
        // deny-list of names, so it covers Inventory, Payments, Shipping
        // and Notifications before any of them exists.
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
