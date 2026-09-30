using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Contracts;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>§12.6's mechanical contract rules, ADR-028's subject rule among them, asserted by reflection.</summary>
/// <remarks>The subject rule asserts an absence, so it ships with positive controls (ADR-028).</remarks>
public class ContractTests
{
    /// <summary>Concrete types only, as a static vocabulary compiles to <c>abstract sealed</c>.</summary>
    private static readonly Type[] Contracts =
    [
        .. typeof(OrderPlaced).Assembly.GetTypes().Where(IsContract)
    ];

    /// <summary>§9.2's shape: <c>Common.Contracts.&lt;Service&gt;.V&lt;n&gt;</c>.</summary>
    private const string VersionedNamespace = @"^Common\.Contracts\.[A-Za-z]+\.V\d+$";

    /// <summary>A concrete, visible type under <c>Common.Contracts</c>, its root included (§9.2).</summary>
    internal static bool IsContract(Type type) =>
        type.IsVisible &&
        type is { IsInterface: false, IsAbstract: false } &&
        type.Namespace is string ns &&
        (ns == "Common.Contracts" || ns.StartsWith("Common.Contracts.", StringComparison.Ordinal));

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.1, on the assembly's references, so it holds for a contract not yet written.
        typeof(OrderPlaced).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_sees_a_contract_that_forgot_its_version_namespace()
    {
        // The predicate, not the assembly, since declaring such a type there would commit the defect.
        IsContract(typeof(Common.Contracts.UnversionedProbe)).ShouldBeTrue(
            "a contract with no version namespace must reach the checks, not slip past them");

        Regex.IsMatch(typeof(Common.Contracts.UnversionedProbe).Namespace!, VersionedNamespace)
            .ShouldBeFalse("and it must then fail the rule it breaks");
    }

    [Fact]
    public void Discovery_sees_a_contract_nested_inside_a_public_type()
    {
        IsContract(typeof(Common.Contracts.NestingProbe.NestedProbe)).ShouldBeTrue(
            "a nested public contract is visible to every consumer, so discovery must see it too");

        typeof(Common.Contracts.NestingProbe.NestedProbe).IsPublic.ShouldBeFalse(
            "and IsPublic is the property that says otherwise — which is why it was the wrong one");
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // §9.2: a contract one namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, VersionedNamespace));
    }

    [Fact]
    public void Every_contract_has_a_sample()
    {
        // Apart from the round-trip, so a failure names the missing sample.
        Type[] unsampled = [.. Contracts.Except(ContractSamples.Sampled)];

        unsampled.ShouldBeEmpty(
            $"every contract needs a ContractSamples entry (§12.6): {Names(unsampled)}");
    }

    [Fact]
    public void No_sample_survives_the_contract_it_was_written_for()
    {
        Type[] orphaned = [.. ContractSamples.Sampled.Except(Contracts)];

        orphaned.ShouldBeEmpty($"these samples name types no longer public contracts: {Names(orphaned)}");
    }

    [Fact]
    public void No_contract_can_be_constructed_half_filled()
    {
        // A dropped required leaves the JSON unchanged, so every writable property is asked (§12.6).
        foreach (Type type in Contracts)
        {
            string[] optional =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.SetMethod is not null && !IsAlwaysSupplied(p, type))
                    // Fully qualified, as §9.2 keeps two versions live during a deprecation.
                    .Select(p => $"{type.FullName}.{p.Name}")
            ];

            // Subtracted from the failures, so no narrowed selection can pass vacuously.
            string[] unexplained = [.. optional.Except(AdditiveMembers)];

            unexplained.ShouldBeEmpty(
                $"{type.FullName} can be constructed without these, so a producer can omit them " +
                "and every consumer reads a default (§12.6)");
        }
    }

    /// <summary>Members added to a live contract, optional for that version's life (§9.2).</summary>
    private static readonly string[] AdditiveMembers =
    [
        // Absent means published before the field existed, which §9.6's saga discards.
        "Common.Contracts.Ordering.V1.OrderCancelled.Origin"
    ];

    [Fact]
    public void A_payload_predating_an_additive_member_still_deserialises()
    {
        // By hand, as a round-trip through today's contract cannot model a producer that never knew the member.
        string beforeTheField = """
            {"MessageId":"0199a1e0-0000-7000-8000-000000000001",
             "CorrelationId":"0199a1e0-0000-7000-8000-000000000002",
             "OccurredAt":"2026-08-25T12:00:00+00:00",
             "OrderId":"0199a1e0-0000-7000-8000-000000000003",
             "CustomerId":"0199a1e0-0000-7000-8000-000000000004",
             "Reason":"customer_request"}
            """;

        OrderCancelled? deserialised = JsonSerializer.Deserialize<OrderCancelled>(beforeTheField);

        deserialised.ShouldNotBeNull();
        deserialised.Origin.ShouldBeNull("absent is what §9.6's discard branch reads");
        deserialised.Reason.ShouldBe(CancelReasons.CustomerRequest);
    }

    [Fact]
    public void Every_additive_member_is_still_additive()
    {
        // A stale entry reads exactly like a live exemption.
        foreach (string entry in AdditiveMembers)
        {
            int split = entry.LastIndexOf('.');
            split.ShouldBeGreaterThan(0, $"{entry} must be spelt Namespace.Type.Member");

            string typeName = entry[..split];
            string memberName = entry[(split + 1)..];

            Type? type = Contracts.SingleOrDefault(t => t.FullName == typeName);
            type.ShouldNotBeNull(
                $"{entry} names no public contract — that version has been retired " +
                "and the entry belongs in the commit that retired it");

            PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            property.ShouldNotBeNull($"{entry} names no member of {type.Name}");

            IsAlwaysSupplied(property, type).ShouldBeFalse(
                $"{entry} is now always supplied, so this entry describes something " +
                "that is no longer true (§12.6)");
        }
    }

    /// <summary>Required, or taken by every public constructor, since one that omits it is a way round.</summary>
    private static bool IsAlwaysSupplied(PropertyInfo property, Type type)
    {
        if (property.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
            return true;

        ConstructorInfo[] constructors = type.GetConstructors();

        return constructors.Length > 0 &&
            constructors.All(c => c.GetParameters().Any(p =>
                string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase) &&
                p.ParameterType == property.PropertyType));
    }

    [Fact]
    public void Every_contract_round_trips_through_the_bus_serialiser()
    {
        // Default options, unlike §9.4's outbox round-trip, as a consumer configures its own serialiser.
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            string json = JsonSerializer.Serialize(instance, type);
            object? returned = JsonSerializer.Deserialize(json, type);

            JsonSerializer.Serialize(returned, type).ShouldBe(json, type.FullName);
        }
    }

    [Fact]
    public void Every_contract_member_reaches_the_wire()
    {
        // A member that never serialises is absent from both round-trip forms (§12.6).
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            using JsonDocument document =
                JsonDocument.Parse(JsonSerializer.Serialize(instance, type));

            string[] onTheWire = [.. document.RootElement.EnumerateObject().Select(p => p.Name)];

            string[] declared =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Select(p => p.Name)
            ];

            onTheWire.ShouldBe(declared, ignoreOrder: true, type.FullName);
        }
    }

    /// <summary>Subject spellings, matched as substrings and incomplete by construction (ADR-028).</summary>
    private static readonly string[] SubjectSpellings =
    [
        "Customer",
        "Buyer",
        "Payer",
        "Subject",
        "User",
        "Principal"
    ];

    /// <summary>The non-event types of a universe that nothing else in it carries.</summary>
    private static Type[] RootsOf(IReadOnlyCollection<Type> universe)
    {
        HashSet<Type> carried = [.. universe.SelectMany(t => CarriedContractTypes(t, universe))];

        return
        [
            .. universe
                .Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
                .Where(t => !carried.Contains(t))
        ];
    }

    /// <summary>The command roots the subject rule judges, from §3.2's Accepts columns (ADR-028).</summary>
    /// <remarks>Declared, as an event carrying a command's type removes it from <see cref="RootsOf"/>.</remarks>
    private static readonly Type[] DeclaredCommandRoots =
    [
        typeof(AuthorisePayment),
        typeof(CancelOrder),
        typeof(ConfirmOrder),
        typeof(MarkOrderShipped),
        typeof(FlagOrderForReview),
        typeof(ReserveStock),
        typeof(ReleaseStock)
    ];

    /// <summary>The non-event contracts that are not command roots, so every contract is classified.</summary>
    private static readonly Type[] DeclaredPayloads =
    [
        typeof(StockLine),          // ReserveStock — judged, a command reaches it
        typeof(PlacedLine),         // OrderPlaced — exempt, only an event reaches it
        typeof(ConfirmedLine)       // OrderConfirmed — the same
    ];

    private static readonly Type[] Commands = JudgedTypesOf(Contracts, DeclaredCommandRoots);

    /// <summary>The roots plus all they carry, over any universe so synthetic types can drive it.</summary>
    private static Type[] JudgedTypesOf(IReadOnlyCollection<Type> universe, Type[] roots)
    {
        HashSet<Type> judged = [.. roots];
        Queue<Type> pending = new(roots);

        while (pending.Count > 0)
        {
            foreach (Type next in CarriedContractTypes(pending.Dequeue(), universe))
            {
                if (judged.Add(next))
                    pending.Enqueue(next);
            }
        }

        return [.. judged];
    }

    private static IEnumerable<Type> CarriedContractTypes(
        Type type,
        IReadOnlyCollection<Type> universe) =>
        type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(p => MembersOfUniverse(p.PropertyType, universe));

    /// <summary>The universe types a type reaches: itself, its element type or any generic argument.</summary>
    private static IEnumerable<Type> MembersOfUniverse(
        Type type,
        IReadOnlyCollection<Type> universe)
    {
        if (universe.Contains(type))
            yield return type;

        if (type.IsArray && type.GetElementType() is Type element)
        {
            foreach (Type reached in MembersOfUniverse(element, universe))
                yield return reached;
        }

        if (!type.IsGenericType)
            yield break;

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type reached in MembersOfUniverse(argument, universe))
                yield return reached;
        }
    }

    private static PropertyInfo[] SubjectMembers(Type type) =>
    [
        .. type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => SubjectSpellings.Any(s =>
                p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
    ];

    [Fact]
    public void No_command_contract_carries_a_subject()
    {
        // §11.4's subject rule on ADR-028's path; events are exempt, and OrderPlaced must keep its CustomerId.
        (string Command, string Member)[] offenders =
        [
            .. Commands
                .SelectMany(t => SubjectMembers(t).Select(p => (t.FullName!, p.Name)))
        ];

        offenders.ShouldBeEmpty(
            "the subject of a money-movement decision is the deciding service's to derive " +
            "from its own record, so a subject here transports an authority the receiver " +
            "already holds — a second source for a decision that must have exactly one " +
            "(ADR-028): " +
            string.Join(", ", offenders.Select(o => $"{o.Command}.{o.Member}")));
    }

    /// <summary>Every member judged commands may carry, so a new one is decided at a red build (ADR-028).</summary>
    private static readonly (Type Contract, string Member)[] ApprovedCommandMembers =
    [
        (typeof(AuthorisePayment), "OrderId"),
        (typeof(AuthorisePayment), "Amount"),      // instruction, not authority
        (typeof(AuthorisePayment), "Currency"),    // the same
        (typeof(CancelOrder), "OrderId"),
        (typeof(CancelOrder), "Reason"),
        (typeof(ConfirmOrder), "OrderId"),
        (typeof(ConfirmOrder), "PaymentReference"),
        (typeof(MarkOrderShipped), "OrderId"),
        (typeof(MarkOrderShipped), "TrackingNumber"),
        (typeof(FlagOrderForReview), "OrderId"),
        (typeof(FlagOrderForReview), "Reason"),
        (typeof(ReserveStock), "OrderId"),
        (typeof(ReserveStock), "Lines"),
        (typeof(ReleaseStock), "OrderId"),
        (typeof(StockLine), "ProductId"),
        (typeof(StockLine), "Quantity")
    ];

    [Fact]
    public void No_command_contract_carries_an_unapproved_member()
    {
        // Per contract, or a member approved on one command would pass on every other.
        (string Command, string Member)[] unapproved =
        [
            .. Commands
                .SelectMany(t => t
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => !ApprovedCommandMembers.Contains((t, p.Name)))
                    .Select(p => (t.FullName!, p.Name)))
        ];

        unapproved.ShouldBeEmpty(
            "a member on a judged command is a decision under ADR-028, so it is approved " +
            "explicitly or it is not there: " +
            string.Join(", ", unapproved.Select(u => $"{u.Command}.{u.Member}")));
    }

    [Fact]
    public void The_approved_member_list_holds_nothing_the_commands_have_dropped()
    {
        (Type Contract, string Member)[] live =
        [
            .. Commands
                .SelectMany(t => t
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => (t, p.Name)))
        ];

        ApprovedCommandMembers.ShouldBeSubsetOf(
            live,
            "an approved pair no command carries is a seat reserved for the next member " +
            "to take without review");
    }

    [Fact]
    public void A_subject_is_detectable_on_a_contract_that_carries_one()
    {
        // The positive control, on the CustomerId ADR-028 requires OrderPlaced to keep.
        SubjectMembers(typeof(OrderPlaced))
            .Select(p => p.Name)
            .ShouldContain(nameof(OrderPlaced.CustomerId));
    }

    /// <summary>The declared vocabulary as theory cases, so a spelling added cannot go unexercised.</summary>
    public static TheoryData<string> DeclaredSpellings => new(SubjectSpellings);

    [Theory]
    [MemberData(nameof(DeclaredSpellings))]
    public void Every_declared_subject_spelling_is_detected(string spelling)
    {
        string[] found =
        [
            .. SubjectMembers(typeof(SubjectGateProbes.EverySpelling)).Select(p => p.Name)
        ];

        found.ShouldContain(
            name => name.Contains(spelling, StringComparison.OrdinalIgnoreCase),
            $"the probe declares a member spelled '{spelling}' and the detector must see it");
    }

    [Fact]
    public void The_spelling_vocabulary_and_its_controls_stay_the_same_size()
    {
        // A spelling removed from the list takes its theory case with it and strands its probe member.
        SubjectSpellings.Length.ShouldBe(
            typeof(SubjectGateProbes.EverySpelling)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Length,
            "every declared spelling needs a probe member, or it is unobserved");
    }

    [Fact]
    public void The_set_the_subject_gate_reads_holds_the_real_commands()
    {
        // Every root by name, not merely a non-empty set, as Commands also holds a payload.
        foreach (Type root in DeclaredCommandRoots)
            Commands.ShouldContain(root);

        // A subject one level down reaches the same decision as a top-level one (ADR-028).
        Commands.ShouldContain(typeof(StockLine));

        Commands.ShouldNotContain(
            typeof(OrderPlaced),
            "events are exempt, and OrderPlaced is the one ADR-028 requires to keep its CustomerId");

        Commands.ShouldNotContain(
            typeof(PlacedLine),
            "a line type an event carries is part of that event, so it inherits the exemption");
    }

    [Fact]
    public void Inferred_command_roots_and_the_declared_list_agree()
    {
        // Inference audits the declared list, so a command added and not declared is caught.
        Type[] inferred = RootsOf(Contracts);

        inferred.ShouldBe(DeclaredCommandRoots, ignoreOrder: true);
    }

    [Fact]
    public void Every_non_event_contract_is_declared_a_command_or_a_payload()
    {
        // A contract only an event carries escapes the root comparison, and dispatch is not structural (§9.1).
        Type[] classified = [.. DeclaredCommandRoots, .. DeclaredPayloads];

        Type[] unclassified =
        [
            .. Contracts
                .Where(t => !typeof(IIntegrationEvent).IsAssignableFrom(t))
                .Where(t => !classified.Contains(t))
        ];

        unclassified.ShouldBeEmpty(
            "a non-event contract is a command or a payload, and which one is a decision " +
            "ADR-028 needs taken rather than inferred: " +
            string.Join(", ", unclassified.Select(t => t.FullName)));

        classified.ShouldBeSubsetOf(
            Contracts,
            "a declared command or payload the assembly no longer holds is a stale entry");
    }

    [Fact]
    public void Inference_alone_loses_a_command_an_event_carries()
    {
        // Pinned as a failing inference, so it goes red the day the declared list becomes ceremony.
        Type[] inferred = RootsOf(SubjectGateProbes.EventCarriesCommandUniverse);

        inferred.ShouldNotContain(
            typeof(SubjectGateProbes.CarriedCommand),
            "inference cannot see this command as a root, which is why the roots are declared");

        Type[] judged = JudgedTypesOf(
            SubjectGateProbes.EventCarriesCommandUniverse,
            [typeof(SubjectGateProbes.CarriedCommand)]);

        judged.ShouldContain(
            typeof(SubjectGateProbes.CarriedCommand),
            "a declared command root is judged whatever an event happens to carry");
    }

    [Fact]
    public void A_payload_shared_by_a_command_and_an_event_stays_judged()
    {
        // Synthetic, as no live payload is shared between a command and an event (ADR-028).
        Type[] judged = JudgedTypesOf(SubjectGateProbes.Universe, RootsOf(SubjectGateProbes.Universe));

        judged.ShouldContain(
            typeof(SubjectGateProbes.SharedLine),
            "a command reaches this type, so the command side's rule applies to it — an event " +
            "also reaching it is what the rejected implementation wrongly treated as an exemption");

        judged.ShouldNotContain(
            typeof(SubjectGateProbes.EventOnlyLine),
            "no command reaches this type, and an event is permitted a subject");

        SubjectMembers(typeof(SubjectGateProbes.SharedLine))
            .Select(p => p.Name)
            .ShouldContain(nameof(SubjectGateProbes.SharedLine.CustomerId));
    }

    [Fact]
    public void A_payload_reached_through_a_two_argument_generic_is_judged()
    {
        CarriedContractTypes(typeof(SubjectGateProbes.ProbeCommand), SubjectGateProbes.Universe)
            .ShouldContain(
                typeof(SubjectGateProbes.SharedLine),
                "every generic argument is part of the payload graph, not only the single one " +
                "a one-argument collection happens to have");
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
