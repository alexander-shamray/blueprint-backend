using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>§8.5's store, on the coordination connection, whose claims no eviction policy drops (§8.1).</summary>
/// <remarks><see cref="RedisKeys.Idempotency"/> owns the prefix; the behaviour passes a key's shape (§8.3).</remarks>
internal sealed class RedisIdempotencyStore(
    [FromKeyedServices(RedisConnections.Coordination)] IConnectionMultiplexer redis,
    RedisKeys redisKeys,
    ILogger<RedisIdempotencyStore> log)
    : IIdempotencyStore
{
    /// <summary>The state written on a claim, which no payload can spell (ADR-057).</summary>
    private const string InProgressMarker = "in-progress";

    /// <summary>Splits the fixed-width token from its state, so a claim stays one <c>SET NX</c>.</summary>
    private const char ClaimSeparator = ':';

    /// <summary><c>Guid.CreateVersion7().ToString("N")</c>, as <c>RedisDistributedLockFactory</c> spells it.</summary>
    private const int TokenLength = 32;

    // GET-compare-SET in one script, so a claim that expired cannot overwrite its successor's entry.
    // KEEPTTL keeps the claim's own window, which therefore starts before §6.3's stamp (§8.5, ADR-038).
    private const string CompleteScript =
        """
        local current = redis.call('get', KEYS[1])
        if current == false or string.sub(current, 1, string.len(ARGV[1])) ~= ARGV[1] then
            return 0
        end
        redis.call('set', KEYS[1], ARGV[2], 'KEEPTTL')
        return 1
        """;

    // Delete only what this claim still owns: an unconditional delete would free a running successor's claim.
    private const string ReleaseScript =
        """
        local current = redis.call('get', KEYS[1])
        if current ~= false and string.sub(current, 1, string.len(ARGV[1])) == ARGV[1] then
            return redis.call('del', KEYS[1])
        end
        return 0
        """;

    private static readonly Action<ILogger, string, Exception?> ReleaseFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(ReleaseFailed)),
            "Idempotency claim {Key} could not be released; it will expire with its retention.");

    private static readonly Action<ILogger, string, Exception?> ClaimLost =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(ClaimLost)),
            "Idempotency claim {Key} was no longer held by this attempt; the write was refused. " +
            "The handler outran its claim's retention (§8.5).");

    public async Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        string token = Guid.CreateVersion7().ToString("N");

        // SET NX: one atomic round trip, so exactly one caller wins.
        bool claimed = await redis
            .GetDatabase()
            .StringSetAsync(redisKeys.Idempotency(key), Value(token, InProgressMarker), retention, When.NotExists);

        return claimed ? token : null;
    }

    public async Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        RedisValue value = await redis.GetDatabase().StringGetAsync(redisKeys.Idempotency(key));

        if (!value.HasValue)
            return null;

        string stored = value.ToString();

        // No token: an entry from before the token existed, still read by the marker test, which stays sound.
        if (stored.Length <= TokenLength || stored[TokenLength] != ClaimSeparator)
        {
            return stored == InProgressMarker
                ? new IdempotencyEntry(true, null)
                : new IdempotencyEntry(false, stored);
        }

        string state = stored[(TokenLength + 1)..];

        return state == InProgressMarker
            ? new IdempotencyEntry(true, null)
            : new IdempotencyEntry(false, state);
    }

    public async Task CompleteAsync(string key, string claim, string payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();

        // No retention to pass: the script preserves the window the claim opened (§8.5).
        RedisResult written = await redis
            .GetDatabase()
            .ScriptEvaluateAsync(
                CompleteScript,
                [redisKeys.Idempotency(key)],
                [Owner(claim), Value(claim, payload)]);

        if ((long)written == 0)
            ClaimLost(log, key, null);
    }

    public async Task ReleaseAsync(string key, string claim, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);

        // No ThrowIfCancellationRequested: a release often follows the caller's own cancellation.
        try
        {
            RedisResult deleted = await redis
                .GetDatabase()
                .ScriptEvaluateAsync(ReleaseScript, [redisKeys.Idempotency(key)], [Owner(claim)]);

            // Logged, not thrown: nothing here can recreate the claim.
            if ((long)deleted == 0)
                ClaimLost(log, key, null);
        }
        catch (RedisException e)
        {
            // Best-effort (§8.5): throwing here would replace the fault the behaviour is rethrowing.
            ReleaseFailed(log, key, e);
        }
    }

    public async Task<IReadOnlyCollection<string>> UnheldAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ct.ThrowIfCancellationRequested();

        if (keys.Count == 0)
            return [];

        IDatabase database = redis.GetDatabase();

        // Materialised, because the answers are zipped back by position.
        string[] candidates = [.. keys];

        // One pipelined EXISTS per key: a multi-key EXISTS is CROSSSLOT on a clustered instance (§8.3).
        bool[] held = await Task.WhenAll(
            candidates.Select(key => database.KeyExistsAsync(redisKeys.Idempotency(key))));

        // Nothing caught: a key reported unheld because its lookup failed would lose a live claim's marker.
        List<string> unheld = [];

        for (int index = 0; index < candidates.Length; index++)
        {
            if (!held[index])
                unheld.Add(candidates[index]);
        }

        return unheld;
    }

    // The token and its separator, so the comparison does not rest on the tokens' fixed width.
    private static RedisValue Owner(string claim) => $"{claim}{ClaimSeparator}";

    private static RedisValue Value(string claim, string state) => $"{claim}{ClaimSeparator}{state}";
}
