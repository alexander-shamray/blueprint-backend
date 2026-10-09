using System.Reflection;
using MassTransit;
using Ordering.Infrastructure.Messaging;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>Every transition the order saga declares is driven by a journey or held by a named suite (§12.1).</summary>
/// <remarks>
/// Read off the machine, so a new <c>During</c> block fails until classified. The undrivable ones are arrivals
/// the broker orders (§12.5).
/// </remarks>
public sealed class SagaCoverageTests
{
    private const BindingFlags OwnPublicMethods =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private const string Harness = "tests/Ordering.Application.Tests/";

    /// <summary>MassTransit's own event on every schedule, which only discards a message from a stale token.</summary>
    private const string AnyReceivedSuffix = ".AnyReceived";

    /// <summary>Transitions an arrival race or a duplicate causes, each with the suite that drives it.</summary>
    private static readonly Dictionary<(string State, string Event), string> InMemory = new()
    {
        // Inventory's release beating the saga's own OrderCancelled (§9.6): the broker orders two publishers.
        [("AwaitingStock", "StockReleased")] = "OrderFulfilmentSagaAwaitingStockTests.cs",
        [("AwaitingPayment", "StockReleased")] = "OrderFulfilmentSagaAwaitingPaymentTests.cs",
        [("AwaitingConfirmation", "StockReleased")] = "OrderFulfilmentSagaAwaitingConfirmationTests.cs",
        [("Confirmed", "StockReleased")] = "OrderFulfilmentSagaConfirmedTests.cs",

        // A state that lasts the length of one message: the saga leaves AwaitingConfirmation on the next event.
        [("AwaitingConfirmation", "OrderCancelled")] = "OrderFulfilmentSagaAwaitingConfirmationTests.cs",

        // Shipping beating Ordering's own acknowledgement (§3.2), which needs the broker to reorder two services.
        [("AwaitingConfirmation", "ShipmentDispatched")] = "OrderFulfilmentSagaAwaitingConfirmationTests.cs",

        // Redeliveries the inbox filter would swallow in a running system (§9.5), delivered to the machine alone.
        [("Confirmed", "OrderConfirmed")] = "OrderFulfilmentSagaConfirmedTests.cs",
        [("Compensating", "OrderConfirmed")] = "OrderFulfilmentSagaCompensatingTests.cs",

        // The second expiry of a wait that was armed twice: the first lapsing is driven, a second would be a clock.
        [("Compensating", "PaymentTimeout.Received")] = "OrderFulfilmentSagaCompensatingTests.cs"
    };

    [Fact]
    public void Every_transition_the_saga_declares_is_driven_by_a_journey_or_held_by_a_named_suite()
    {
        HashSet<(string State, string Event)> declared = Declared();
        Dictionary<(string State, string Event), List<string>> driven = Driven();

        foreach ((string State, string Event) pair in declared)
        {
            bool journey = driven.ContainsKey(pair);
            bool memory = InMemory.ContainsKey(pair);

            (journey || memory).ShouldBeTrue(
                $"{pair.State} / {pair.Event} is declared by the saga and no journey covers it and no suite holds it");
            (journey && memory).ShouldBeFalse(
                $"{pair.State} / {pair.Event} is both driven by a journey and listed as one that cannot be");
        }
    }

    [Fact]
    public void Nothing_is_claimed_for_a_transition_the_saga_does_not_declare()
    {
        HashSet<(string State, string Event)> declared = Declared();

        foreach ((string State, string Event) pair in Driven().Keys.Concat(InMemory.Keys))
        {
            declared.ShouldContain(pair, $"{pair.State} / {pair.Event} is claimed and the saga has no such transition");
        }
    }

    [Fact]
    public void Each_suite_named_for_a_transition_exists_and_mentions_its_event()
    {
        string root = SimulatorMappings.RepositoryRoot();

        foreach (((string state, string name), string file) in InMemory)
        {
            string path = Path.Combine(root, Harness + file);
            File.Exists(path).ShouldBeTrue($"{state} / {name} names {file}, which is not under {Harness}");
            file.ShouldContain(state, Case.Sensitive, $"{file} is the suite for another state than {state}");

            // The event's property, not its wire type: a schedule's is named for the wait, e.g. PaymentTimeout.
            string mention = name.Split('.')[0];
            File.ReadAllText(path).ShouldContain(
                mention,
                Case.Sensitive,
                $"{file} is named for {state} / {name} and never mentions {mention}");
        }
    }

    [Fact]
    public void Every_journey_that_claims_a_transition_is_a_test()
    {
        foreach (MethodInfo method in CoveringMethods())
            method.GetCustomAttribute<FactAttribute>().ShouldNotBeNull($"{method.DeclaringType!.Name}.{method.Name}");
    }

    private static HashSet<(string State, string Event)> Declared()
    {
        OrderFulfilmentSaga saga = new();
        HashSet<(string State, string Event)> declared = [];

        foreach (State state in saga.States)
        {
            foreach (Event @event in saga.NextEvents(state))
            {
                if (!@event.Name.EndsWith(AnyReceivedSuffix, StringComparison.Ordinal))
                    declared.Add((state.Name, @event.Name));
            }
        }

        // A machine read as empty would hold every claim to nothing, which every claim passes.
        declared.Count.ShouldBeGreaterThan(20);

        return declared;
    }

    private static Dictionary<(string State, string Event), List<string>> Driven()
    {
        Dictionary<(string State, string Event), List<string>> driven = [];

        foreach (MethodInfo method in CoveringMethods())
        {
            foreach (CoversAttribute covers in method.GetCustomAttributes<CoversAttribute>())
            {
                (string, string) pair = (covers.State, covers.Event);
                if (!driven.TryGetValue(pair, out List<string>? by))
                    driven[pair] = by = [];

                by.Add($"{method.DeclaringType!.Name}.{method.Name}");
            }
        }

        return driven;
    }

    private static IEnumerable<MethodInfo> CoveringMethods() =>
        typeof(CoversAttribute).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(OwnPublicMethods))
            .Where(method => method.GetCustomAttributes<CoversAttribute>().Any());
}
