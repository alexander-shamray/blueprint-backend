using Microsoft.Extensions.Hosting;

namespace Common.Infrastructure.Redis;

/// <summary>§8.3's half-key rule, prefixed by <see cref="IHostEnvironment.ApplicationName"/> verbatim.</summary>
/// <remarks>No <c>Cache(string)</c>, which would double <c>InstanceName</c>'s prefix (§8.3).</remarks>
public sealed class RedisKeys(IHostEnvironment environment)
{
    private readonly string _service = environment.ApplicationName;

    /// <summary>"{service}:cache:" — consumed by <c>RedisCacheOptions</c> only.</summary>
    public string CacheInstanceName => $"{_service}:cache:";

    /// <summary>"{service}:lock:{name}" — the noeviction keyspace (§8.1).</summary>
    public string Lock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{_service}:lock:{name}";
    }

    /// <summary>"{service}:idem:{suffix}" — the noeviction keyspace (§8.1, §8.5).</summary>
    public string Idempotency(string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return $"{_service}:idem:{suffix}";
    }
}
