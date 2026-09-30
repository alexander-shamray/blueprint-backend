namespace Common.Application;

/// <summary>A contract value an <see cref="ICommandMessageMapper{TMessage, TCommand}"/> cannot map.</summary>
/// <remarks>Excluded from retry: a malformed contract does not parse itself on a later attempt (§9.8).</remarks>
public sealed class ContractMappingException : Exception
{
    public ContractMappingException()
    {
    }

    public ContractMappingException(string message)
        : base(message)
    {
    }

    public ContractMappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
