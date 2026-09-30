namespace Common.Application;

/// <summary>Carries §8.5's key to §6.3's transaction, so the key's shape is built in one place only.</summary>
/// <remarks>Empty unless the command opted in; §6.3 reads it once, before a nested dispatch can replace it.</remarks>
public sealed class IdempotencyContext
{
    /// <summary>The key claimed for this scope's command, or null if none was.</summary>
    public string? Key { get; private set; }

    public void Claim(string key) => Key = key;

    /// <summary>Called from a <c>finally</c>, so the key lives for exactly the dispatch that claimed it.</summary>
    public void Clear() => Key = null;
}
