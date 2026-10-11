using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Contracts;

/// <summary>Each service's <see cref="ContractContext"/> held to the contracts it is for, in both directions (§12.6).</summary>
/// <remarks>A service with no context would be sampled by no one, and a stale one blocks its retirement (ADR-062).</remarks>
public class ContractContextTests
{
    private static string[] ServicesWithContracts =>
        [.. ContractShapes.Contracts().Select(ContractContexts.ServiceOf).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_service_with_a_contract_has_a_context()
    {
        string[] declared = [.. ContractContexts.All.Select(c => c.Service)];

        string[] missing = [.. ServicesWithContracts.Except(declared)];

        missing.ShouldBeEmpty(
            "a service's contracts are sampled and judged from its ContractContext, in Contracts/<Service>, and " +
            $"these have none: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_context_is_for_a_service_that_still_has_contracts()
    {
        string[] stale = [.. ContractContexts.All.Select(c => c.Service).Except(ServicesWithContracts)];

        stale.ShouldBeEmpty(
            "a context whose service has no contract left is the folder to delete with it (ADR-062): " +
            string.Join(", ", stale));
    }

    [Fact]
    public void No_service_has_two_contexts()
    {
        ContractContexts.All
            .GroupBy(c => c.Service)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ShouldBeEmpty("a service's context is one class, or its entries cannot be found in one place");
    }

    [Fact]
    public void No_contract_is_sampled_by_two_contexts()
    {
        // Apart from the registry, which keeps the first so this can name the pair instead of failing to load.
        Type[] twice =
        [
            .. ContractContexts.All
                .SelectMany(c => c.Samples.Keys)
                .GroupBy(t => t)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
        ];

        twice.ShouldBeEmpty($"a contract has one sample, in its own service's context: {string.Join(", ", twice)}");
    }

    [Fact]
    public void Every_type_a_context_names_is_a_contract()
    {
        string[] notContracts =
        [
            .. ContractContexts.All.SelectMany(c => ClaimedBy(c)
                .Except(ContractShapes.Contracts())
                .Select(t => $"{c.Service} names {t.FullName}"))
        ];

        notContracts.ShouldBeEmpty("a root, payload, sample or approved member names a public contract or nothing");
    }

    [Fact]
    public void Every_context_names_only_its_own_services_contracts()
    {
        string[] foreign =
        [
            .. ContractContexts.All.SelectMany(c => ClaimedBy(c)
                .Where(t => ContractContexts.ServiceOf(t) != c.Service)
                .Select(t => $"{c.Service} names {t.FullName}"))
        ];

        foreign.ShouldBeEmpty(
            "an entry for another service's contract goes missing when that service is retired, " +
            "and it is the folder next to the contract that deletes it with it");
    }

    [Fact]
    public void Every_additive_member_is_named_in_its_own_services_namespace()
    {
        string[] foreign =
        [
            .. ContractContexts.All.SelectMany(c => c.AdditiveMembers
                .Where(m => !m.StartsWith($"Common.Contracts.{c.Service}.", StringComparison.Ordinal))
                .Select(m => $"{c.Service} lists {m}"))
        ];

        foreign.ShouldBeEmpty("an additive member belongs to the context of the service whose contract it is on");
    }

    private static IEnumerable<Type> ClaimedBy(ContractContext context) =>
    [
        .. context.Samples.Keys,
        .. context.CommandRoots,
        .. context.JudgedPayloads,
        .. context.EventPayloads,
        .. context.ApprovedCommandMembers.Select(m => m.Contract)
    ];
}
