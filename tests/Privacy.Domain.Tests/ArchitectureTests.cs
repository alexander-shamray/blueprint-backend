using Privacy.Domain.ErasureRequests;
using Shouldly;
using Xunit;

namespace Privacy.Domain.Tests;

/// <summary>§4.2's first gate, by reflection, since the rule is about assembly references.</summary>
public class ArchitectureTests
{
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections is the aggregate's responder set and the event list it inherits.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections"];

        IEnumerable<string> referenced = typeof(ErasureRequest).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
}
