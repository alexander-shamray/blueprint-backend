using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Common.Infrastructure.Redis;

/// <summary>
/// §8.5's store, over the <b>coordination</b> connection rather than the cache
/// one. The distinction is the point: an idempotency claim on an
/// <c>allkeys-lru</c> instance is evicted under exactly the memory pressure
/// that makes a duplicate write hardest to reproduce (§8.1).
/// </summary>
/// <remarks>
/// <see cref="RedisKeys.Idempotency"/> supplies the <c>{service}:idem:</c>
/// prefix the ACL requires, from <c>ApplicationName</c> — the same single
/// source §13.2 stamps on every trace, so the Redis prefix and the telemetry
/// label cannot disagree (§8.3). The behaviour passes a key <em>shape</em> and
/// this class owns the keyspace; prefixing on both sides would double it.
/// </remarks>
internal sealed class RedisIdempotencyStore(
    [FromKeyedServices(RedisConnections.Coordination)] IConnectionMultiplexer redis,
    RedisKeys redisKeys,
    ILogger<RedisIdempotencyStore> log)
    : IIdempotencyStore
{
    /// <summary>
    /// The state written on a claim, and what tells an unfinished attempt from
    /// a recorded outcome.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not valid JSON, which is what makes the test
    /// unambiguous.</b> Every payload this store holds is
    /// <c>JsonSerializer</c> output: a string arrives quoted, a number is
    /// digits, an object is braced, and the void case is the four characters
    /// <c>null</c>. No serialised value can spell this, so a payload can never
    /// be misread as an in-progress marker or the reverse. A sentinel that
    /// happened to be valid JSON would put that collision one unlucky result
    /// away.
    /// </remarks>
    private const string InProgressMarker = "in-progress";

    /// <summary>
    /// What separates the claim token from the state it owns, inside one
    /// string value.
    /// </summary>
    /// <remarks>
    /// <b>A hash would have kept the two apart without a separator, and was
    /// not taken.</b> The claim has to be a single atomic write against a key
    /// that may not exist — <c>SET NX</c> with a TTL is one operation, where
    /// the hash spelling is <c>HSETNX</c> plus <c>EXPIRE</c> and a claim that
    /// dies between them is a key with no expiry at all. Keeping one string
    /// value keeps the claim one round trip and keeps the marker argument
    /// above intact.
    /// <para>
    /// The token is 32 hex characters and can therefore contain no separator,
    /// so the split is unambiguous from the left even though a JSON payload on
    /// the right may hold as many colons as it likes.
    /// </para>
    /// </remarks>
    private const char ClaimSeparator = ':';

    /// <summary>
    /// <c>Guid.CreateVersion7().ToString("N")</c>, the spelling
    /// <c>RedisDistributedLockFactory</c> already uses.
    /// </summary>
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

    // Delete only what this claim still owns, and this is the half of #127
    // that is worse in kind than the overwrite: an unconditional delete frees
    // a SUCCESSOR's claim while that successor is still running, which admits
    // a concurrent duplicate rather than corrupting the record of one.
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

        // SET NX — one round trip, and the atomicity the port's contract names.
        // A read-then-write here would admit both callers of the race this
        // exists to let exactly one caller win.
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

        // An entry carrying no token was written by a release before #127, is
        // still inside its retention, and is read by the SAME test the store
        // used before the token existed: the marker means in progress and
        // anything else is a recorded outcome. That test is exactly as sound
        // as it was, because the marker is deliberately not valid JSON.
        //
        // **Reporting the whole unparseable class as in progress was the first
        // shape of this and it was wrong**, in a way "both answers decline the
        // duplicate commit" concealed: a replay is not a commit. A completed
        // pre-token entry read as in-progress answers 409 to a retry of work
        // that succeeded, for the rest of the retention — and then lets the
        // command run a second time once the key expires. During a rolling
        // deploy, which is the only window this branch exists for, that is
        // both halves of what §8.5 promises, broken at once.
        //
        // The write side needs no matching case: both scripts compare a token
        // this value does not carry, so they no-op and log rather than
        // clobbering it.
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

        // No ThrowIfCancellationRequested, and that is the one asymmetry in
        // this class. The behaviour already passes CancellationToken.None here
        // — the commonest reason to be releasing at all is the caller's own
        // cancellation, and honouring the token would abandon the release and
        // leak the claim for a day.
        try
        {
            RedisResult deleted = await redis
                .GetDatabase()
                .ScriptEvaluateAsync(ReleaseScript, [redisKeys.Idempotency(key)], [Owner(claim)]);

            // Not an error and not silent either. Nothing here can recreate
            // the claim, and the caller is already reporting a fault of its
            // own on the path that reaches this — so the honest report is a
            // log line naming the key, on the same terms as ReleaseFailed
            // below.
            if ((long)deleted == 0)
                ClaimLost(log, key, null);
        }
        catch (RedisException e)
        {
            // Best-effort, and this is the site §8.5's callout names: the
            // behaviour calls this from a catch block before `throw;`, so an
            // exception raised here would replace the fault the caller was
            // already reporting with a Redis one — the original destroyed
            // rather than wrapped. Swallowing it in the behaviour would be a
            // silence with nothing to report it; here there is a logger.
            //
            // The cost of swallowing is bounded and worth stating: the claim
            // stays in progress, so every retry of this CommandId meets
            // ConcurrentRequestException until the retention expires.
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

        // Materialised once, because the answers are zipped back against this
        // by position and an enumerable is not promised to be the same
        // sequence twice.
        string[] candidates = [.. keys];

        // One EXISTS per key rather than one command over all of them, and the
        // keyspace decides that rather than the round trips. These are issued
        // without awaiting between them, so StackExchange.Redis pipelines them
        // onto the one connection and the batch costs about what a single
        // multi-key command would. A genuine multi-key EXISTS is one command
        // whose keys must share a hash slot, and §8.3's prefix leaves
        // {subject}:{operation}:{commandId} varying — so every key hashes
        // somewhere different, and on a clustered coordination instance that
        // form is a CROSSSLOT error rather than an optimisation.
        bool[] held = await Task.WhenAll(
            candidates.Select(key => database.KeyExistsAsync(redisKeys.Idempotency(key))));

        // Nothing is caught here, and the omission is the port's "answer for
        // every key or throw". A key reported unheld because its lookup failed
        // is a marker deleted while its claim is alive — the duplicate #171 is
        // about, arriving through the mechanism that closes it. The caller
        // keeps every marker when this throws, which is the direction that
        // costs a purge rather than a guarantee.
        List<string> unheld = [];

        for (int index = 0; index < candidates.Length; index++)
        {
            if (!held[index])
                unheld.Add(candidates[index]);
        }

        return unheld;
    }

    // What the scripts compare against: the token AND its separator, so a
    // token cannot match by being a prefix of a longer one. The tokens are
    // fixed-width, so that cannot happen today — the separator is what keeps
    // the comparison structural rather than dependent on that.
    private static RedisValue Owner(string claim) => $"{claim}{ClaimSeparator}";

    private static RedisValue Value(string claim, string state) => $"{claim}{ClaimSeparator}{state}";
}
