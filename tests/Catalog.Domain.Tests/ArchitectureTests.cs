using Catalog.Domain.Products;
using Shouldly;
using Xunit;

namespace Catalog.Domain.Tests;

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
        // prefix still passes System.Data.SqlClient or a serialiser. Each
        // BCL assembly Domain starts using earns its line here on purpose —
        // extending this list is the decision the gate exists to force, and
        // System.Text.Json is the extension the table forbids by name.
        //
        // System.Collections earned its line with the first domain event: a
        // record's generated equality goes through EqualityComparer<T>, which
        // lives there. No collection type appears in any domain signature.
        // System.Linq earned its line with Money's currency guard —
        // enumerable logic over owned values is domain work, not an I/O
        // dependency, and §5.4's Order sample already leans on it.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Product).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
