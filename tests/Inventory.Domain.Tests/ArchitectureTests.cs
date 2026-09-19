using Inventory.Domain.Stock;
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
        // Common.Domain and System.Runtime are what an empty domain
        // references. System.Collections carries a domain event's generated
        // record equality through EqualityComparer<T>. System.Linq is
        // Reservation enumerating its own lines to enforce an invariant,
        // which is domain work over owned values, not an I/O dependency.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(StockItem).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
