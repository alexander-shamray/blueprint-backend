namespace Common.Application;

/// <summary>A key whose command completed was sent again carrying a different command (ADR-057).</summary>
/// <remarks>An exception, not an <see cref="Error"/>: no domain decided anything (§10.5).</remarks>
public sealed class CommandIdReusedException(Guid commandId)
    : Exception($"Command {commandId} was already used for a different request.")
{
    /// <summary>The reused <c>CommandId</c>, for the log line and nothing else.</summary>
    public Guid CommandId { get; } = commandId;
}
