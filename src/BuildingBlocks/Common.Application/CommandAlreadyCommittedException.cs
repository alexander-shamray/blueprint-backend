namespace Common.Application;

/// <summary>The command under this key already committed, and its outcome cannot be returned (ADR-037).</summary>
/// <remarks>Shares <see cref="ConcurrentRequestException"/>'s 409; §10.5's <c>code</c> tells the two apart.</remarks>
public sealed class CommandAlreadyCommittedException(string key)
    : Exception("A command with this identifier has already been committed.")
{
    /// <summary>For the log line only: it carries the subject segment (§8.5), so no response describes it.</summary>
    public string Key { get; } = key;
}
