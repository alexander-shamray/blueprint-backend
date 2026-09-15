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

/// <summary>
/// §12.6's contract rules, each mechanical enough to be a test rather than a
/// review note: §9.1's "a contract may not name a domain type", §9.2's
/// versioned namespace, <c>required</c> members, and ADR-028's subject rule.
/// </summary>
/// <remarks>
/// The subject rule asserts an absence, so an empty result is what success and
/// a broken detector both look like; it therefore ships with positive controls.
/// </remarks>
public class ContractTests
{
    /// <summary>
    /// Concrete types only: <see cref="IIntegrationEvent"/> (§9.1) and the
    /// static code vocabularies would otherwise be asked for a versioned
    /// namespace and a sample.
    /// </summary>
    /// <remarks>
    /// <c>IsAbstract: false</c> excludes the vocabularies too, because a C#
    /// <c>static class</c> compiles to <c>abstract sealed</c>. The root
    /// namespace is included: a type declared straight into
    /// <c>Common.Contracts</c> is the unversioned contract §9.2 rejects, and a
    /// trailing dot would hide it.
    /// </remarks>
    private static readonly Type[] Contracts =
    [
        .. typeof(OrderPlaced).Assembly.GetTypes().Where(IsContract)
    ];

    /// <summary>§9.2's shape: <c>Common.Contracts.&lt;Service&gt;.V&lt;n&gt;</c>.</summary>
    private const string VersionedNamespace = @"^Common\.Contracts\.[A-Za-z]+\.V\d+$";

    /// <summary>
    /// A concrete type visible outside the assembly, anywhere under
    /// <c>Common.Contracts</c>, the root included.
    /// </summary>
    /// <remarks>
    /// <c>IsVisible</c> rather than <c>IsPublic</c>: a public type nested in a
    /// public class reports <c>IsNestedPublic</c>, and is as reachable by a
    /// consumer as any other.
    /// </remarks>
    internal static bool IsContract(Type type) =>
        type.IsVisible &&
        type is { IsInterface: false, IsAbstract: false } &&
        type.Namespace is string ns &&
        (ns == "Common.Contracts" || ns.StartsWith("Common.Contracts.", StringComparison.Ordinal));

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.1's rule. Checked at the assembly level because a contract cannot
        // name a domain type without the project reference, so the assertion
        // holds for a contract that has not been written yet.
        typeof(OrderPlaced).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_sees_a_contract_that_forgot_its_version_namespace()
    {
        // The positive control for the discovery predicate: a concrete type
        // declared straight into `Common.Contracts` is the unversioned contract
        // §9.2 forbids, and a predicate that cannot see it leaves every check
        // here green. Asserted against the predicate rather than the assembly,
        // because declaring such a type in Common.Contracts would be committing
        // the defect to prove it can be caught.
        IsContract(typeof(Common.Contracts.UnversionedProbe)).ShouldBeTrue(
            "a contract with no version namespace must reach the checks, not slip past them");

        Regex.IsMatch(typeof(Common.Contracts.UnversionedProbe).Namespace!, VersionedNamespace)
            .ShouldBeFalse("and it must then fail the rule it breaks");
    }

    [Fact]
    public void Discovery_sees_a_contract_nested_inside_a_public_type()
    {
        // The positive control for `IsVisible`: `Type.IsPublic` is false for a
        // public type nested in a public class, which is as reachable by a
        // consumer as any other.
        IsContract(typeof(Common.Contracts.NestingProbe.NestedProbe)).ShouldBeTrue(
            "a nested public contract is visible to every consumer, so discovery must see it too");

        typeof(Common.Contracts.NestingProbe.NestedProbe).IsPublic.ShouldBeFalse(
            "and IsPublic is the property that says otherwise — which is why it was the wrong one");
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.2. A contract that lands one
        // namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, VersionedNamespace));
    }

    [Fact]
    public void Every_contract_has_a_sample()
    {
        // The precondition for the round-trip below, asserted separately so a
        // failure names the missing sample rather than arriving mid-loop.
        Type[] unsampled = [.. Contracts.Except(ContractSamples.Sampled)];

        unsampled.ShouldBeEmpty(
            $"every contract needs a ContractSamples entry (§12.6): {Names(unsampled)}");
    }

    [Fact]
    public void No_sample_survives_the_contract_it_was_written_for()
    {
        // The other direction, and the one throwing cannot catch: a sample for
        // a deleted or renamed contract compiles until the type is gone and is
        // dead weight the moment it is. Cheap here, invisible otherwise.
        Type[] orphaned = [.. ContractSamples.Sampled.Except(Contracts)];

        orphaned.ShouldBeEmpty($"these samples name types no longer public contracts: {Names(orphaned)}");
    }

    [Fact]
    public void No_contract_can_be_constructed_half_filled()
    {
        // §12.6 calls `required` members mechanical. No serialisation test can
        // see one dropped, because the JSON is unchanged; what breaks is a
        // producer's ability to omit the member. A positional record can still
        // declare an init property beside its constructor parameters, so the
        // question is asked of every writable property rather than of the
        // type: `required`, or supplied by every public constructor.
        foreach (Type type in Contracts)
        {
            string[] optional =
            [
                .. type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.SetMethod is not null && !IsAlwaysSupplied(p, type))
                    // Fully qualified, because §9.2 has two versions of a
                    // contract live at once during a deprecation, and a simple
                    // name cannot say which one an exemption is about.
                    .Select(p => $"{type.FullName}.{p.Name}")
            ];

            // Subtracted from the failures rather than from the candidates, so
            // there is no narrowed selection to pass vacuously.
            string[] unexplained = [.. optional.Except(AdditiveMembers)];

            unexplained.ShouldBeEmpty(
                $"{type.FullName} can be constructed without these, so a producer can omit them " +
                "and every consumer reads a default (§12.6)");
        }
    }

    /// <summary>
    /// Members added to a contract that was already live. They are optional
    /// for the life of that contract version, and the entry clears when the
    /// version does (§9.2).
    /// </summary>
    /// <remarks>
    /// §9.2 makes a new optional field additive, and <c>System.Text.Json</c>
    /// refuses a payload missing a <c>required</c> member, so a member shipped
    /// <c>required</c> faults every payload that predates it — and a payload
    /// can outlive its outbox row in the error queue or return by replay.
    /// Tightening the member is a breaking change inside the version, which
    /// §9.2 sends to a new one.
    /// </remarks>
    private static readonly string[] AdditiveMembers =
    [
        // Absent means "published before this field existed", and §9.6's saga
        // discards on it. It leaves this list when V1 is retired.
        "Common.Contracts.Ordering.V1.OrderCancelled.Origin"
    ];

    [Fact]
    public void A_payload_predating_an_additive_member_still_deserialises()
    {
        // §9.6 discards an OrderCancelled whose Origin is absent, and that
        // branch is reachable only if the payload deserialises at all;
        // System.Text.Json refuses a missing `required` member outright. A
        // hand-written payload rather than a sample with the field removed,
        // because a round-trip through today's contract cannot model a
        // producer that never knew the member.
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
        // The gate on the list: an entry that names no public contract, or a
        // member that has become always-supplied, reads exactly like a live
        // exemption. Keyed by the fully qualified name because §9.2 has two
        // versions live at once during a deprecation, and a simple name cannot
        // say which the entry is about.
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

    /// <summary>
    /// Whether a property cannot be left unset: either it is <c>required</c>,
    /// or every public constructor takes it.
    /// </summary>
    /// <remarks>
    /// Every constructor, not any: one overload that omits the parameter is
    /// one way to build the contract without the value.
    /// </remarks>
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
        // Default options on purpose, unlike §9.4's outbox round-trip: the
        // outbox is this service's own format and takes the registered
        // OutboxJson, while a contract crosses to a consumer that configures
        // its own serialiser. A contract that needs a converter to survive has
        // stopped being primitives.
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
        // The half the round-trip cannot see: a member that fails to serialise
        // at all is absent from both forms and the comparison passes. Asking
        // for the declared names is what closes that.
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

    /// <summary>
    /// The spellings a subject identifier has reached this repository under,
    /// matched as a substring of the property name.
    /// </summary>
    /// <remarks>
    /// A list, and therefore incomplete by construction, which is why the gate
    /// ships with a positive control and an allow-list beside it.
    /// </remarks>
    private static readonly string[] SubjectSpellings =
    [
        "Customer",
        "Buyer",
        "Payer",
        "Subject",
        "User",
        "Principal"
    ];

    /// <summary>
    /// The command roots of a type universe: a member of it that is not an
    /// event and that nothing else in it carries.
    /// </summary>
    /// <remarks>
    /// Nothing carries <c>ReserveStock</c>, so it is a root; <c>StockLine</c>
    /// is carried by it and is therefore a payload.
    /// </remarks>
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

    /// <summary>
    /// The command roots the subject rule judges — §3.2's Accepts columns read
    /// across, declared rather than inferred.
    /// </summary>
    /// <remarks>
    /// The judged set is built up from these roots rather than subtracted from
    /// the contracts: §9.1 says only that a command does not implement
    /// <see cref="IIntegrationEvent"/>, an event is permitted the subject
    /// ADR-028 requires <c>OrderPlaced</c> to keep, and a payload a command and
    /// an event both carry is still judged, because the command side forbids
    /// what the event side permits. Inference alone fails open, since an event
    /// declaring a property of a command's type removes that command from
    /// <see cref="RootsOf"/>; the list is the assertion and inference audits it.
    /// </remarks>
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

    /// <summary>
    /// Every non-event contract that is not a command root — the payload
    /// records.
    /// </summary>
    /// <remarks>
    /// Whether a type is dispatched as a command is not a fact the type system
    /// holds, since §9.1 defines a command by what it does not implement. What
    /// can be settled is that every contract has been classified by somebody:
    /// a type in neither list fails the build.
    /// </remarks>
    private static readonly Type[] DeclaredPayloads =
    [
        typeof(StockLine),          // ReserveStock — judged, a command reaches it
        typeof(PlacedLine),         // OrderPlaced — exempt, only an event reaches it
        typeof(ConfirmedLine)       // OrderConfirmed — the same
    ];

    private static readonly Type[] Commands = JudgedTypesOf(Contracts, DeclaredCommandRoots);

    /// <summary>
    /// The judged set of a type universe: its command roots, plus everything
    /// those carry transitively.
    /// </summary>
    /// <remarks>
    /// A function of a universe rather than a fixed field, so the same
    /// algorithm can be driven over synthetic types; the real contracts have
    /// no shared payload to pin the regression on.
    /// </remarks>
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

    /// <summary>
    /// Every type of the universe a member's declared type reaches — itself,
    /// an array's element type, or any of a generic's arguments.
    /// </summary>
    /// <remarks>
    /// All the arguments, not the single one: a member typed
    /// <c>IReadOnlyDictionary&lt;string, SomePayload&gt;</c> would otherwise
    /// leave <c>SomePayload</c> outside the closure and its subject unjudged.
    /// </remarks>
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
        // §11.4's subject rule on the path ADR-028 settled: the subject of a
        // money-movement decision is the deciding service's to derive from its
        // own record, so a subject on a command is a second source for a
        // decision that must have exactly one. Events are exempt and must be:
        // OrderPlaced carries the CustomerId that is the record Payments
        // builds from.
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

    /// <summary>
    /// Every member the judged commands are approved to carry. A gate, not a
    /// description: a name absent from here fails the build.
    /// </summary>
    /// <remarks>
    /// The subject rule is a deny-list, so <c>OwnerId</c> walks past it. This
    /// buys a forced decision rather than a verdict: a new member cannot be
    /// added silently, and approving one is a reviewer's decision at a red
    /// build.
    /// </remarks>
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
        // The allow-list half of ADR-028's rule: it does not decide whether a
        // new member is a subject, it makes the question unavoidable. Scoped to
        // the contract that approved it rather than to the name, or
        // `PaymentReference` approved for ConfirmOrder would silently permit it
        // on AuthorisePayment.
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
        // The other direction, and the reason it is not optional: an entry
        // left behind by a removed member is a name pre-approved for whatever
        // arrives under it next, which is a deny-list hole reintroduced inside
        // the allow-list that replaced one.
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
        // The positive control: the deny-list test passes if SubjectMembers
        // matches nothing at all, and an empty offender set reads identically
        // either way. OrderPlaced is the event Payments builds its record of
        // the payer from (§3.2), so it is the one contract whose CustomerId
        // ADR-028 requires to stay.
        SubjectMembers(typeof(OrderPlaced))
            .Select(p => p.Name)
            .ShouldContain(nameof(OrderPlaced.CustomerId));
    }

    /// <summary>
    /// The declared vocabulary, as theory cases, so a spelling added to the
    /// list cannot be left unexercised.
    /// </summary>
    public static TheoryData<string> DeclaredSpellings => new(SubjectSpellings);

    [Theory]
    [MemberData(nameof(DeclaredSpellings))]
    public void Every_declared_subject_spelling_is_detected(string spelling)
    {
        // The cases are generated from the list rather than copied beside it:
        // a second copy of the vocabulary is one a new entry can be left out
        // of, and the entry is then never exercised while nothing says so.
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
        // The other direction of the pairing: the theory enumerates the list,
        // so a spelling removed from it takes its case with it and could leave
        // a stranded probe member behind. Pinning the probe to the list is
        // what sees that.
        SubjectSpellings.Length.ShouldBe(
            typeof(SubjectGateProbes.EverySpelling)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Length,
            "every declared spelling needs a probe member, or it is unobserved");
    }

    [Fact]
    public void The_set_the_subject_gate_reads_holds_the_real_commands()
    {
        // The control above proves the detector works and says nothing about
        // what it is pointed at: a discovery that finds no roots leaves the
        // subject test vacuous and green. Not merely non-empty, because
        // Commands legitimately holds a payload, so every declared root is
        // named.
        foreach (Type root in DeclaredCommandRoots)
            Commands.ShouldContain(root);

        // A payload only a command carries stays judged: a subject one level
        // down reaches the same decision as a top-level one.
        Commands.ShouldContain(typeof(StockLine));

        // And the exemptions must actually be excluded, or the gate is being
        // applied to events — which ADR-028 permits a subject.
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
        // The declared list is what the gate judges, so inference is the other
        // copy that audits it: a command added to the contracts and not
        // declared here is an inferred root nobody listed, and a command an
        // event carries drops out of RootsOf while the declared list keeps
        // judging it.
        Type[] inferred = RootsOf(Contracts);

        inferred.ShouldBe(DeclaredCommandRoots, ignoreOrder: true);
    }

    [Fact]
    public void Every_non_event_contract_is_declared_a_command_or_a_payload()
    {
        // The pairing above has one blind spot: a contract carried only by an
        // event and absent from DeclaredCommandRoots drops out of both sides,
        // so the equality holds while no gate inspects it. Whether such a type
        // is dispatched is not decidable structurally (§9.1); whether a human
        // has classified it is.
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

        // And the other direction, on the allow-list's own argument: a
        // classification for a type the assembly has dropped is a seat
        // reserved for whatever takes the name next.
        classified.ShouldBeSubsetOf(
            Contracts,
            "a declared command or payload the assembly no longer holds is a stale entry");
    }

    [Fact]
    public void Inference_alone_loses_a_command_an_event_carries()
    {
        // Pinned as a failing inference: this test's subject is the hole, so it
        // goes red on the day inference stops having it and the declared list
        // becomes ceremony.
        Type[] inferred = RootsOf(SubjectGateProbes.EventCarriesCommandUniverse);

        inferred.ShouldNotContain(
            typeof(SubjectGateProbes.CarriedCommand),
            "inference cannot see this command as a root, which is why the roots are declared");

        // And the declared-root path judges it anyway, which is the property
        // the real gate depends on.
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
        // The live contracts have no payload shared between a command and an
        // event, so an implementation that exempted the shared shape because
        // an event reached it would stay green over them. Synthetic types are
        // the only way to hold that closed.
        Type[] judged = JudgedTypesOf(SubjectGateProbes.Universe, RootsOf(SubjectGateProbes.Universe));

        judged.ShouldContain(
            typeof(SubjectGateProbes.SharedLine),
            "a command reaches this type, so the command side's rule applies to it — an event " +
            "also reaching it is what the rejected implementation wrongly treated as an exemption");

        // The other direction, in the same universe: an exemption that must
        // survive, or the fix for the false negative would have reinstated the
        // false positive it replaced.
        judged.ShouldNotContain(
            typeof(SubjectGateProbes.EventOnlyLine),
            "no command reaches this type, and an event is permitted a subject");

        // And the gate must actually see the subject once the type is judged,
        // which is the step that turns membership into a build failure.
        SubjectMembers(typeof(SubjectGateProbes.SharedLine))
            .Select(p => p.Name)
            .ShouldContain(nameof(SubjectGateProbes.SharedLine.CustomerId));
    }

    [Fact]
    public void A_payload_reached_through_a_two_argument_generic_is_judged()
    {
        // `ProbeCommand` carries its payload as
        // IReadOnlyDictionary<string, SharedLine>, which a closure that unwraps
        // only single-argument generics would miss. The test above already
        // fails on that; this one names which defect came back.
        CarriedContractTypes(typeof(SubjectGateProbes.ProbeCommand), SubjectGateProbes.Universe)
            .ShouldContain(
                typeof(SubjectGateProbes.SharedLine),
                "every generic argument is part of the payload graph, not only the single one " +
                "a one-argument collection happens to have");
    }

    private static string Names(IEnumerable<Type> types) =>
        string.Join(", ", types.Select(t => t.FullName));
}
