using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>§4.2's first gate, by reflection, since the rule is about assembly references.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections is the records' generated equality; System.Linq is the value objects'
        // letter scans and Order's Aggregate over its lines.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Order).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
