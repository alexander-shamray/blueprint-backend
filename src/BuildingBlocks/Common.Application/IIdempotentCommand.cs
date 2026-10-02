namespace Common.Application;

/// <summary>Opts into <see cref="IdempotencyBehavior{TCommand,TResult}"/>, which reads both members.</summary>
/// <remarks>A command without it is never protected; one with it needs an authenticated endpoint (§8.5).</remarks>
public interface IIdempotentCommand
{
    /// <summary>
    /// The key's middle segment, declared so a rename cannot change it and unique within the service (§8.5).
    /// </summary>
    static abstract string OperationName { get; }

    /// <summary>Names the act the client wants done once: a field, since §4.2 keeps HTTP out of Application.</summary>
    Guid CommandId { get; }
}
