namespace Common.Application;

/// <summary>Maps an inbound command contract to its application command, found by the §6.2 scan.</summary>
/// <remarks>Explicit, because the contract is a published, versioned type its service owns (§3.2).</remarks>
public interface ICommandMessageMapper<in TMessage, out TCommand>
    where TMessage : class
{
    /// <summary>Throws <see cref="ContractMappingException"/> for a value it cannot map.</summary>
    TCommand Map(TMessage message);
}
