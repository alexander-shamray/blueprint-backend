namespace Platform.IntegrationTests.Contracts;

/// <summary>One sample per contract, from each service's <see cref="ContractContext"/>, so a contract with none fails (§12.6).</summary>
/// <remarks>Every member distinct and non-default, or a zeroed sample hides a dropped member (§12.6).</remarks>
internal static class ContractSamples
{
    private static readonly Dictionary<Type, Func<object>> Registry =
        ContractContexts.All
            .SelectMany(c => c.Samples)
            .GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);

    /// <summary>The sample for one contract, or a failure naming it, never a null the suite could skip.</summary>
    public static object Create(Type contract) =>
        Registry.TryGetValue(contract, out Func<object>? sample) ? sample()
            : throw new InvalidOperationException(
                $"No sample for the contract '{contract.FullName}'. A V1 contract's members are " +
                "required apart from any additive member §12.6 lists, so nothing can construct " +
                "one by reflection — " +
                "add an entry to its service's ContractContext.");

    /// <summary>The types a sample exists for, so a sample outliving its contract is caught too.</summary>
    public static IReadOnlyCollection<Type> Sampled => Registry.Keys;
}
