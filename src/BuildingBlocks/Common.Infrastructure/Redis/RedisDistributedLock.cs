using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

internal sealed class RedisDistributedLock(IConnectionMultiplexer redis, string key, string name, string token)
    : IDistributedLock
{
    // GET-compare-DEL as one script, or a lock expiring between them deletes the next holder's key (§8.1).
    private const string ReleaseScript =
        """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        end
        return 0
        """;

    private readonly Lock _gate = new();
    private Task? _release;

    public string Name { get; } = name;

    public async ValueTask DisposeAsync()
    {
        // Every disposer awaits the same release, reset only on failure so a caller may retry the idempotent script.
        Task release;
        lock (_gate)
        {
            _release ??= ReleaseAsync();
            release = _release;
        }

        try
        {
            await release;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_release, release))
                    _release = null;
            }

            throw;
        }
    }

    private async Task ReleaseAsync() =>
        await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token]);
}
