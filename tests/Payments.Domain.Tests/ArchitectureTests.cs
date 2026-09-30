using Payments.Domain.Intents;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

/// <summary>§4.2's first gate, by reflection, since the rule is about assembly references.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections is OrderId's, whose generated equality goes through EqualityComparer<T>.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections"];

        IEnumerable<string> referenced = typeof(PaymentIntent).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
