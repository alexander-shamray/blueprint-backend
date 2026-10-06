using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.4's persisted type name, a column two deployments may read differently.</summary>
public class MessageTypeMapTests
{
    private static MessageTypeMap Map() => new([typeof(SampleDomainEvent).Assembly]);

    [Fact]
    public void The_persisted_name_carries_no_assembly_and_no_version()
    {
        // AssemblyQualifiedName embeds a version a release bumps, stranding every earlier row.
        string name = Map().NameOf(typeof(SampleDomainEvent));

        name.ShouldBe("Common.Infrastructure.Tests.SampleDomainEvent");
        name.ShouldNotContain("Version=");
        name.ShouldNotContain("Culture=");
    }

    [Fact]
    public void A_name_resolves_back_to_its_type()
    {
        MessageTypeMap map = Map();

        map.Resolve(map.NameOf(typeof(SampleIntegrationEvent))).ShouldBe(typeof(SampleIntegrationEvent));
    }

    [Fact]
    public void Naming_an_unstageable_type_throws()
    {
        // In the transaction, so the command fails rather than staging an undeliverable row.
        Should
            .Throw<InvalidOperationException>(() => Map().NameOf(typeof(NotAMessage)))
            .Message.ShouldContain(nameof(NotAMessage));
    }

    [Fact]
    public void Resolving_an_unknown_name_throws_and_says_what_to_do()
    {
        // On the dispatcher, where the message naming the departed type lands in the retry log.
        Should
            .Throw<InvalidOperationException>(() => Map().Resolve("Gone.Away.Event"))
            .Message.ShouldContain("drain the outbox");
    }

    [Fact]
    public void A_value_type_domain_event_is_in_the_map()
    {
        Map()
            .NameOf(typeof(SampleValueTypeDomainEvent))
            .ShouldBe("Common.Infrastructure.Tests.SampleValueTypeDomainEvent");
    }

    [Fact]
    public void A_non_ascii_type_name_round_trips_through_the_map()
    {
        MessageTypeMap map = Map();

        map.Resolve(map.NameOf(typeof(CommandeCréée))).ShouldBe(typeof(CommandeCréée));
    }

    [Fact]
    public void An_alias_resolves_to_the_type_that_replaced_it()
    {
        // §9.4's rename: both names resolve for one release.
        MessageTypeMap map = new(
            [typeof(SampleDomainEvent).Assembly],
            new Dictionary<string, Type> { ["Old.Namespace.SampleDomainEvent"] = typeof(SampleDomainEvent) });

        map.Resolve("Old.Namespace.SampleDomainEvent").ShouldBe(typeof(SampleDomainEvent));

        // Outward, NameOf writes the current name, so the old one drains.
        map.NameOf(typeof(SampleDomainEvent)).ShouldBe("Common.Infrastructure.Tests.SampleDomainEvent");
    }

    [Fact]
    public void The_compatibility_release_writes_the_old_name_and_resolves_both()
    {
        // Release one of §9.4's rename: every instance writes the name all of them can read.
        const string old = "Old.Namespace.SampleDomainEvent";

        MessageTypeMap map = new(
            [typeof(SampleDomainEvent).Assembly],
            new Dictionary<string, Type> { [old] = typeof(SampleDomainEvent) },
            new Dictionary<Type, string> { [typeof(SampleDomainEvent)] = old });

        map.NameOf(typeof(SampleDomainEvent)).ShouldBe(old);
        map.Resolve(old).ShouldBe(typeof(SampleDomainEvent));
        map.Resolve("Common.Infrastructure.Tests.SampleDomainEvent").ShouldBe(typeof(SampleDomainEvent));
    }

    [Fact]
    public void Writing_a_name_the_map_cannot_resolve_fails_the_host()
    {
        // Without the alias, this instance would stage rows it could not deliver.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap(
                    [typeof(SampleDomainEvent).Assembly],
                    new Dictionary<string, Type>(),
                    new Dictionary<Type, string> { [typeof(SampleDomainEvent)] = "Nothing.Resolves.This" }))
            .Message.ShouldContain("cannot resolve");
    }

    [Fact]
    public void Writing_a_name_that_resolves_to_another_type_fails_the_host()
    {
        // A failure with no symptom: the payload would read back as a type it never was.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap(
                    [typeof(SampleDomainEvent).Assembly],
                    new Dictionary<string, Type>(),
                    new Dictionary<Type, string>
                    {
                        [typeof(SampleDomainEvent)] = typeof(SampleValueTypeDomainEvent).FullName!
                    }))
            .Message.ShouldContain("resolves to");
    }

    [Fact]
    public void An_alias_longer_than_the_column_fails_the_host()
    {
        // An alias is typed by hand, so it is the name that can exceed the column.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap(
                    [typeof(SampleDomainEvent).Assembly],
                    new Dictionary<string, Type>
                    {
                        [new string('n', MessageTypeMap.MaxNameLength + 1)] = typeof(SampleDomainEvent)
                    }))
            .Message.ShouldContain("No row can carry it");
    }

    [Fact]
    public void An_alias_onto_a_type_the_map_does_not_carry_fails_the_host()
    {
        // The dispatcher trusts the row's Lane, so this alias would bypass the lane guards.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap(
                    [typeof(SampleDomainEvent).Assembly],
                    new Dictionary<string, Type> { ["Some.Old.Name"] = typeof(NotAMessage) }))
            .Message.ShouldContain("does not carry");
    }

    [Fact]
    public void An_alias_that_shadows_a_live_name_fails_the_host()
    {
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap(
                    [typeof(SampleDomainEvent).Assembly],
                    new Dictionary<string, Type>
                    {
                        ["Common.Infrastructure.Tests.SampleDomainEvent"] = typeof(SampleIntegrationEvent)
                    }))
            .Message.ShouldContain("also a live type name");
    }

    [Fact]
    public void An_assembly_listed_twice_fails_the_host()
    {
        // A test host re-adding a production assembly is the realistic collision, refused at startup.
        Should
            .Throw<InvalidOperationException>(() =>
                new MessageTypeMap([typeof(SampleDomainEvent).Assembly, typeof(SampleDomainEvent).Assembly]))
            .Message.ShouldContain("cannot distinguish");
    }

    [Fact]
    public void Stageable_domain_events_are_the_domain_events_and_not_the_contracts()
    {
        // §12.4 round-trips this set, so a domain event missing from it is never checked.
        IEnumerable<Type> stageable = Map().StageableDomainEvents;

        stageable.ShouldContain(typeof(SampleDomainEvent));
        stageable.ShouldNotContain(typeof(SampleIntegrationEvent));
    }
}
