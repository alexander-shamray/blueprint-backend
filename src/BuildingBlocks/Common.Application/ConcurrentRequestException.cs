namespace Common.Application;

/// <summary>A second request arrived under a key whose first attempt has not finished.</summary>
/// <remarks>An exception, not an <see cref="Error"/>: no domain decided anything (§10.5).</remarks>
public sealed class ConcurrentRequestException(Guid commandId)
    : Exception($"A request is already in progress for command {commandId}.")
{
    /// <summary>The contended <c>CommandId</c>, for the log line and nothing else.</summary>
    public Guid CommandId { get; } = commandId;
}
