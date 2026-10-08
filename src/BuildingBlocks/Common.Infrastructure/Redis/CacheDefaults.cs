namespace Common.Infrastructure.Redis;

/// <summary>§8.2's default entry options for every host's <c>HybridCache</c>.</summary>
public static class CacheDefaults
{
    /// <summary>L2, in Redis.</summary>
    public static readonly TimeSpan Expiration = TimeSpan.FromMinutes(10);

    /// <summary>L1, in process: how stale a replica may serve after another one invalidated (§8.2).</summary>
    public static readonly TimeSpan LocalCacheExpiration = TimeSpan.FromMinutes(1);

    public const int MaximumPayloadBytes = 1024 * 1024;
}
