using Shouldly;
using Xunit;
using Shapes = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Platform.IntegrationTests;

/// <summary>§9.2's breaking-change rule, held against the shape each contract was recorded with.</summary>
public class ContractShapeTests
{
    [Fact]
    public void No_contract_breaks_its_recorded_shape()
    {
        string[] breaks = ContractShapes.Breaks(ContractShapes.Recorded(), ContractShapes.Live());

        breaks.ShouldBeEmpty(
            "a breaking change is a new version (§9.2); a contract with no consumer is changed in place under " +
            $"an ADR, and {ContractShapes.FileName} is replaced in the commit that argues it: " +
            string.Join("; ", breaks));
    }

    [Fact]
    public void The_recorded_shapes_are_the_live_ones()
    {
        string live = ContractShapes.Serialise(ContractShapes.Live());
        File.WriteAllText(ContractShapes.Received, live);

        ContractShapes.Serialise(ContractShapes.Recorded()).ShouldBe(
            live,
            $"{ContractShapes.FileName} is not the contracts' shape: replace it from {ContractShapes.Received} " +
            "and commit it with the change");
    }

    [Fact]
    public void The_gate_reads_every_concrete_type_the_contract_assembly_exports()
    {
        // A new service's namespace is held from its first contract, with no list to extend.
        string[] exported =
        [
            .. typeof(Common.Contracts.Ordering.V1.OrderPlaced).Assembly
                .GetExportedTypes()
                .Where(t => t is { IsInterface: false, IsAbstract: false })
                .Select(t => t.FullName!)
        ];

        exported.ShouldNotBeEmpty();
        ContractShapes.Live().Keys.ShouldBe(exported, ignoreOrder: true);
    }

    [Fact]
    public void A_member_is_recorded_with_its_type_its_nullability_and_whether_it_is_required()
    {
        Dictionary<string, string> recorded = ContractShapes.Of([typeof(Probe)])[typeof(Probe).FullName!];

        recorded.ShouldBe(
            new Dictionary<string, string>
            {
                ["Positional"] = "required System.Guid",
                ["Text"] = "required System.String",
                ["Optional"] = "System.String?",
                ["Count"] = "System.Int32?",
                ["Labels"] = "required System.Collections.Generic.IReadOnlyList<System.String?>",
                ["Map"] = "required System.Collections.Generic.Dictionary<System.String, System.Int32[]>"
            },
            ignoreOrder: true);
    }

    /// <summary>The changes the comparer is asked about, with whether each one breaks a consumer.</summary>
    private static readonly Dictionary<string, (Action<Dictionary<string, string>> Change, bool Breaks)> Changes =
        new()
        {
            ["a member removed"] = (m => m.Remove("Optional"), true),
            ["a member renamed"] = (m => { m.Remove("Id"); m["Identifier"] = "required System.Guid"; }, true),
            ["a member's type changed"] = (m => m["Id"] = "required System.String", true),
            ["a member made nullable"] = (m => m["Id"] = "required System.Guid?", true),
            ["an optional member made required"] = (m => m["Optional"] = "required System.String?", true),
            ["a required member made optional"] = (m => m["Id"] = "System.Guid", true),
            ["a required member added"] = (m => m["Added"] = "required System.Int32", true),
            ["an optional member added"] = (m => m["Added"] = "System.Int32?", false)
        };

    public static TheoryData<string> ChangeNames => new(Changes.Keys);

    [Theory]
    [MemberData(nameof(ChangeNames))]
    public void The_comparer_breaks_on_exactly_the_changes_a_consumer_fails_on(string name)
    {
        (Action<Dictionary<string, string>> change, bool breaks) = Changes[name];
        Shapes live = Baseline();
        change(live["Contract"]);

        (ContractShapes.Breaks(Baseline(), live).Length > 0).ShouldBe(breaks, name);
    }

    [Fact]
    public void The_comparer_breaks_on_a_removed_contract_and_not_on_an_added_one()
    {
        ContractShapes.Breaks(Baseline(), []).ShouldHaveSingleItem();

        Shapes added = Baseline();
        added["Another"] = new Dictionary<string, string> { ["Id"] = "required System.Guid" };

        ContractShapes.Breaks(Baseline(), added).ShouldBeEmpty();
    }

    private static Shapes Baseline() =>
        new()
        {
            ["Contract"] = new()
            {
                ["Id"] = "required System.Guid",
                ["Optional"] = "System.String?"
            }
        };

    public sealed record Probe(Guid Positional)
    {
        public required string Text { get; init; }

        public string? Optional { get; init; }

        public int? Count { get; init; }

        public required IReadOnlyList<string?> Labels { get; init; }

        public required Dictionary<string, int[]> Map { get; init; }
    }
}
