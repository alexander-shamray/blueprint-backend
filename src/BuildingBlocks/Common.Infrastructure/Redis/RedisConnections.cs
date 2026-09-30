namespace Common.Infrastructure.Redis;

/// <summary>The keyed names of §8.1's two connections, spelled as their configuration keys (§14.1).</summary>
public static class RedisConnections
{
    public const string Cache = "RedisCache";
    public const string Coordination = "RedisCoordination";
}
