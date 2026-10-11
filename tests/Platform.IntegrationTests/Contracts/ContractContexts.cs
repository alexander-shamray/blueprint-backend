using Common.Contracts;

namespace Platform.IntegrationTests.Contracts;

/// <summary>Every <see cref="ContractContext"/> in the test assembly, found rather than listed.</summary>
internal static class ContractContexts
{
    /// <summary>A new service's context is held from its first file, with no list to extend.</summary>
    internal static readonly ContractContext[] All =
    [
        .. typeof(ContractContext).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsAssignableTo(typeof(ContractContext)))
            .Select(t => (ContractContext)Activator.CreateInstance(t)!)
            .OrderBy(c => c.Service, StringComparer.Ordinal)
    ];

    /// <summary>The service a contract belongs to, the <c>&lt;Service&gt;</c> of its namespace (§9.2).</summary>
    internal static string ServiceOf(Type contract)
    {
        string[] segments = contract.Namespace!.Split('.');

        return segments.Length > 2 ? segments[2] : string.Empty;
    }

    /// <summary>The assembly the contracts are in, named by a type it holds whatever services exist.</summary>
    internal static System.Reflection.Assembly Assembly => typeof(IIntegrationEvent).Assembly;
}
