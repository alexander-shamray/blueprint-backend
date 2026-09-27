using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>
/// §4.2's first gate: Domain references only <c>Common.Domain</c> and the
/// framework. Plain reflection, no NetArchTest: the rule is about assembly
/// references, not type dependencies, and <c>GetReferencedAssemblies</c>
/// asks exactly that question.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // The dependency table's rule is an allow-list — "Common.Domain and
        // nothing else" — so the gate is one too, and an exact one: a
        // blacklist only bans what someone thought to name, and a System.*
        // prefix still passes System.Data.SqlClient or a serialiser.
        // System.Collections is the typed ids' — a readonly record struct's
        // generated equality goes through EqualityComparer<T> — and the
        // shipment's list of tracking events; System.Linq is the
        // deduplication over that list, domain work over owned values rather
        // than an I/O dependency.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Shipment).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
