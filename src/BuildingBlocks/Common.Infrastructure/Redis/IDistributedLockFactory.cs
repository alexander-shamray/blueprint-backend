namespace Common.Infrastructure.Redis;

/// <summary>§8.1's lock: <c>SET key NX PX</c>, token-checked release, and no overload without a TTL.</summary>
public interface IDistributedLockFactory
{
    /// <summary>Null when the lock is held elsewhere; throws when Redis is unreachable (§8.1).</summary>
    Task<IDistributedLock?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct = default);
}

/// <summary>A held lock, released on disposal only if its token still owns the key.</summary>
public interface IDistributedLock : IAsyncDisposable
{
    string Name { get; }
}
