using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Application;
using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="CommandFingerprintRule"/> names, over commands shaped for the purpose (ADR-057).</summary>
public class CommandFingerprintRuleTests
{
    public enum Colour
    {
        Red,
        Green
    }

    public sealed record Line(Guid ProductId, int Quantity);

    public sealed record Postal(string Line1, string? Line2);

    public sealed record WellFormed(
        Guid CommandId,
        string Name,
        decimal? Amount,
        Colour Colour,
        DateTimeOffset At,
        IReadOnlyList<Line> Lines,
        Postal Address,
        Guid[] Ids,
        bool Flag,
        DateTime When,
        DateOnly Day,
        TimeOnly Time,
        TimeSpan Span,
        Uri Link,
        IList<Line> Listed,
        List<Guid> Concrete,
        ImmutableArray<Guid> Frozen,
        ImmutableList<Guid> FrozenList,
        IImmutableList<Guid> FrozenInterface,
        Colour? Shade) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.well-formed";
    }

    public sealed record Positional(Guid CommandId, string Name) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.positional";
    }

    // Internal because CA1051 refuses a visible instance field; reflection reads its public members all the same.
    internal sealed class WithAField(Guid commandId, string? note) : ICommand<Result>, IIdempotentCommand
    {
        public string? Note = note;

        public Guid CommandId { get; } = commandId;

        public static string OperationName => "probe.field";
    }

    public sealed record WithAnIgnoredProperty(Guid CommandId, [property: JsonIgnore] string? Note)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.ignored";
    }

    public sealed record WithANeverIgnoredProperty(
        Guid CommandId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.never-ignored";
    }

    public abstract record Noted
    {
        [JsonIgnore]
        public virtual string? Note { get; init; }
    }

    public sealed record InheritsAnIgnoredNote(Guid CommandId) : Noted, ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.inherits-ignored";
    }

    public sealed record OverridesAnIgnoredNote(Guid CommandId, string? Note)
        : Noted, ICommand<Result>, IIdempotentCommand
    {
        public override string? Note { get; init; } = Note;

        public static string OperationName => "probe.overrides-ignored";
    }

    // A domain value object: its state is private, so the serialiser writes it as an empty object.
    public sealed class Sku
    {
        private readonly string code;

        public Sku(string code) => this.code = code;

        public override string ToString() => code;
    }

    public readonly record struct Tally
    {
        private readonly int count;

        public Tally(int count) => this.count = count;

        public override string ToString() => count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed record WithAnOpaqueValue(Guid CommandId, Sku Sku) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.opaque";
    }

    public sealed record WithAnOpaqueStruct(Guid CommandId, Tally? Tally) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.opaque-struct";
    }

    public interface IShape
    {
        string Name { get; }
    }

    public abstract record Payment(decimal Amount);

    public record Card(decimal Amount, string Last4) : Payment(Amount);

    public sealed record WithAnInterface(Guid CommandId, IShape Shape) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.interface";
    }

    public sealed record WithAnAbstractBase(Guid CommandId, Payment Payment) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.abstract";
    }

    public sealed record WithAnUnsealedClass(Guid CommandId, Card Card) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.unsealed";
    }

    public sealed record WithAnObject(Guid CommandId, object Payload) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.object";
    }

    public sealed record WithAHashSet(Guid CommandId, HashSet<Guid> Ids) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.hash-set";
    }

    public sealed record WithASet(Guid CommandId, ISet<Guid> Ids) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.set";
    }

    public sealed record WithADictionary(Guid CommandId, Dictionary<string, string> Ids)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.dictionary";
    }

    public sealed record WithAnEnumerable(Guid CommandId, IEnumerable<Guid> Ids) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.enumerable";
    }

    public sealed record WithACollection(Guid CommandId, IReadOnlyCollection<Guid> Ids)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.collection";
    }

    public readonly record struct StructWithAHashSet(Guid CommandId, HashSet<Guid> Ids)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.struct-hash-set";
    }

    public sealed record Basket(string Name, HashSet<Guid> Ids);

    public sealed record Entry(Guid Id, IShape Shape);

    public sealed record WithANestedOffender(Guid CommandId, Basket Basket) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.nested";
    }

    public sealed record WithAnOffendingElement(Guid CommandId, IReadOnlyList<Entry> Entries)
        : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.element";
    }

    public sealed record Node(string Name, Node? Next, IReadOnlyList<Node> Children);

    public sealed record WithACycle(Guid CommandId, Node Root) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.cycle";
    }

    public sealed class WithPrivateState : ICommand<Result>, IIdempotentCommand
    {
        public static readonly object Shared = new();

        private readonly object? note;

        private object? sink;

        public WithPrivateState(Guid commandId, object? note)
        {
            CommandId = commandId;
            this.note = note;
        }

        public Guid CommandId { get; }

        public static string OperationName => "probe.private";

        private object? Hidden => note;

        public object? Sink
        {
            set => sink = value;
        }

        public object? this[int index] => index == 0 ? Hidden : sink;

        public override string ToString() => Hidden?.ToString() ?? string.Empty;
    }

    private static readonly string[] BadCommands =
    [
        nameof(WithAField),
        nameof(WithAnIgnoredProperty),
        nameof(InheritsAnIgnoredNote),
        nameof(WithAnOpaqueValue),
        nameof(WithAnOpaqueStruct),
        nameof(WithAnInterface),
        nameof(WithAnAbstractBase),
        nameof(WithAnUnsealedClass),
        nameof(WithAnObject),
        nameof(WithAHashSet),
        nameof(WithASet),
        nameof(WithADictionary),
        nameof(WithAnEnumerable),
        nameof(WithACollection),
        nameof(StructWithAHashSet),
        nameof(WithANestedOffender),
        nameof(WithAnOffendingElement)
    ];

    private static readonly string[] WellFormedCommands =
    [
        nameof(WellFormed),
        nameof(Positional),
        nameof(WithACycle),
        nameof(WithPrivateState),
        nameof(OverridesAnIgnoredNote),
        nameof(WriteEndpointRuleTests.Reached),
        nameof(WriteEndpointRuleTests.Built),
        nameof(WriteEndpointRuleTests.Unreached),
        nameof(WriteEndpointRuleTests.Counted),
        "Keyed"
    ];

    [Fact]
    public void A_command_made_of_members_the_fingerprint_sees_has_no_offender()
    {
        CommandFingerprintRule.Offenders(typeof(WellFormed)).ShouldBeEmpty();
    }

    [Fact]
    public void A_public_field_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAField))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAField.Note is a public field");
    }

    [Fact]
    public void A_property_marked_json_ignore_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAnIgnoredProperty))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnIgnoredProperty.Note is marked [JsonIgnore]");
    }

    [Fact]
    public void A_json_ignore_is_refused_even_on_a_condition_the_serialiser_always_writes()
    {
        JsonSerializer.Serialize(new WithANeverIgnoredProperty(Guid.NewGuid(), "n")).ShouldContain("Note");

        CommandFingerprintRule
            .Offenders(typeof(WithANeverIgnoredProperty))
            .ShouldHaveSingleItem()
            .ShouldBe(
                "WithANeverIgnoredProperty.Note is marked [JsonIgnore], refused whatever its condition (ADR-057)");
    }

    [Fact]
    public void An_ignored_base_property_is_named_where_the_serialiser_omits_it_and_only_there()
    {
        // CommandFingerprint's options, restated.
        JsonSerializerOptions options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

        JsonSerializer.Serialize(new InheritsAnIgnoredNote(Guid.NewGuid()) { Note = "n" }, options)
            .ShouldNotContain("Note");
        CommandFingerprintRule
            .Offenders(typeof(InheritsAnIgnoredNote))
            .ShouldHaveSingleItem()
            .ShouldStartWith("InheritsAnIgnoredNote.Note is marked [JsonIgnore]");

        JsonSerializer.Serialize(new OverridesAnIgnoredNote(Guid.NewGuid(), "n"), options).ShouldContain("Note");
        CommandFingerprintRule.Offenders(typeof(OverridesAnIgnoredNote)).ShouldBeEmpty();
    }

    [Fact]
    public void A_member_whose_type_exposes_no_public_property_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAnOpaqueValue))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnOpaqueValue.Sku is a Sku, which exposes no public property");

        CommandFingerprintRule
            .Offenders(typeof(WithAnOpaqueStruct))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnOpaqueStruct.Tally is a Tally, which exposes no public property");
    }

    [Fact]
    public void A_member_declared_as_an_interface_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAnInterface))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnInterface.Shape is declared as IShape, which is not sealed");
    }

    [Fact]
    public void A_member_declared_as_an_abstract_or_an_unsealed_class_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAnAbstractBase))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnAbstractBase.Payment is declared as Payment, which is not sealed");

        CommandFingerprintRule
            .Offenders(typeof(WithAnUnsealedClass))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnUnsealedClass.Card is declared as Card, which is not sealed");
    }

    [Fact]
    public void A_member_declared_as_object_is_named()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithAnObject))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnObject.Payload is declared as object, so what it holds cannot be read here");
    }

    [Theory]
    [InlineData(typeof(WithAHashSet), "HashSet<Guid>")]
    [InlineData(typeof(WithASet), "ISet<Guid>")]
    [InlineData(typeof(WithADictionary), "Dictionary<String, String>")]
    [InlineData(typeof(WithAnEnumerable), "IEnumerable<Guid>")]
    [InlineData(typeof(WithACollection), "IReadOnlyCollection<Guid>")]
    public void A_collection_whose_type_promises_no_order_is_named(Type command, string declared)
    {
        CommandFingerprintRule
            .Offenders(command)
            .ShouldHaveSingleItem()
            .ShouldStartWith(
                $"{command.Name}.Ids is declared as {declared}, a collection whose type promises no order");
    }

    [Fact]
    public void A_struct_command_is_read_as_a_class_one_is()
    {
        CommandFingerprintRule
            .Offenders(typeof(CommandFingerprintRuleTests).Assembly)
            .ShouldContain(offender => offender.StartsWith(
                "StructWithAHashSet.Ids is declared as HashSet<Guid>, a collection",
                StringComparison.Ordinal));
    }

    [Fact]
    public void The_operation_names_are_read_from_a_struct_command_as_from_a_class_one()
    {
        IReadOnlyList<string> names =
            CommandFingerprintRule.OperationNames(typeof(CommandFingerprintRuleTests).Assembly);

        names.ShouldContain(StructWithAHashSet.OperationName);
        names.ShouldContain(Positional.OperationName);
    }

    [Fact]
    public void A_bad_member_one_level_down_is_named_with_its_path()
    {
        CommandFingerprintRule
            .Offenders(typeof(WithANestedOffender))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithANestedOffender.Basket.Ids is declared as HashSet<Guid>, a collection");

        CommandFingerprintRule
            .Offenders(typeof(WithAnOffendingElement))
            .ShouldHaveSingleItem()
            .ShouldStartWith("WithAnOffendingElement.Entries[].Shape is declared as IShape, which is not sealed");
    }

    [Fact]
    public void A_type_that_holds_itself_is_walked_once_and_is_no_offender()
    {
        CommandFingerprintRule.Offenders(typeof(WithACycle)).ShouldBeEmpty();
    }

    [Fact]
    public void Non_public_static_write_only_and_indexed_members_are_not_walked()
    {
        // A record's backing fields and EqualityContract are non-public, so a positional record adds nothing.
        CommandFingerprintRule.Offenders(typeof(Positional)).ShouldBeEmpty();
        CommandFingerprintRule.Offenders(typeof(WithPrivateState)).ShouldBeEmpty();
    }

    [Fact]
    public void The_assembly_form_names_every_bad_command_and_no_well_formed_one()
    {
        IReadOnlyList<string> offenders =
            CommandFingerprintRule.Offenders(typeof(CommandFingerprintRuleTests).Assembly);

        foreach (string command in BadCommands)
            offenders.ShouldContain(offender => offender.StartsWith($"{command}.", StringComparison.Ordinal));

        foreach (string command in WellFormedCommands)
            offenders.ShouldNotContain(offender => offender.StartsWith($"{command}.", StringComparison.Ordinal));

        string[] order = [.. offenders.Select(offender => offender[..offender.IndexOf('.', StringComparison.Ordinal)])];
        order.ShouldBe([.. order.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void The_assembly_form_over_an_assembly_with_no_idempotent_command_is_empty()
    {
        CommandFingerprintRule.Offenders(typeof(IIdempotentCommand).Assembly).ShouldBeEmpty();
    }
}
