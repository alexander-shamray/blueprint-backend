using Inventory.Domain.Stock;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

/// <summary>§4.2's first gate, by reflection, since the rule is about assembly references.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections carries generated record equality through EqualityComparer<T>, and System.Linq is
        // Reservation checking its own lines.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(StockItem).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
