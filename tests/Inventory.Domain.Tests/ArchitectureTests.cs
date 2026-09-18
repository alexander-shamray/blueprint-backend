using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

/// <summary>
/// §4.2's first gate, a CI failure from the first template commit rather than
/// a review convention. Plain reflection, no NetArchTest: the rule is about
/// assembly references, not type dependencies, and
/// <c>GetReferencedAssemblies</c> asks exactly that question.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // The dependency table's rule is an allow-list — "Common.Domain and
        // nothing else" — so the gate is one too, and an exact one: a
        // blacklist only bans what someone thought to name, and a System.*
        // prefix still passes System.Data.SqlClient or a serialiser. Each
        // BCL assembly Domain starts using earns its line here on purpose —
        // extending this list is the decision the gate exists to force, and
        // System.Text.Json is the extension the table forbids by name.
        //
        // Two entries, because two is what an empty domain references. The
        // two that usually follow, and what earns each: System.Collections
        // with the first domain event, whose generated record equality goes
        // through EqualityComparer<T>, and System.Linq with the first value
        // object doing enumerable logic over owned values — domain work,
        // not an I/O dependency.
        string[] allowed = ["Common.Domain", "System.Runtime"];

        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
