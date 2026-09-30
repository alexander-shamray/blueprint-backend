using System.Collections.Concurrent;

namespace Common.Application.Tests;

/// <summary>An in-memory, token-checked store whose call log is the assertion surface for §8.5's paths.</summary>
internal sealed class RecordingIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, Held> _entries = new();

    /// <summary>Every call, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The <see cref="CancellationToken"/> each call was handed, by call name.</summary>
    public Dictionary<string, CancellationToken> Tokens { get; } = [];

    /// <summary>Set to throw from <see cref="CompleteAsync"/>, for the hold case.</summary>
    public Exception? CompleteFault { get; set; }

    /// <summary>The token the last successful claim handed back.</summary>
    public string? MintedToken { get; private set; }

    /// <summary>The token the last complete or release was called with.</summary>
    public string? WrittenUnder { get; private set; }

    public IReadOnlyDictionary<string, IdempotencyEntry> Entries =>
        _entries.ToDictionary(pair => pair.Key, pair => pair.Value.Entry);

    public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct)
    {
        Calls.Add($"claim {key}");
        Tokens["claim"] = ct;

        string token = Guid.CreateVersion7().ToString("N");
        bool claimed = _entries.TryAdd(key, new Held(token, new IdempotencyEntry(true, null)));

        if (claimed)
            MintedToken = token;

        return Task.FromResult(claimed ? token : null);
    }

    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct)
    {
        Calls.Add($"get {key}");
        Tokens["get"] = ct;
        return Task.FromResult(_entries.TryGetValue(key, out Held? held) ? held.Entry : null);
    }

    public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct)
    {
        Calls.Add($"complete {key}");
        Tokens["complete"] = ct;
        WrittenUnder = claim;

        if (CompleteFault is not null)
            return Task.FromException(CompleteFault);

        if (Owns(key, claim))
            _entries[key] = new Held(claim, new IdempotencyEntry(false, payload));

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, string claim, CancellationToken ct)
    {
        Calls.Add($"release {key}");
        Tokens["release"] = ct;
        WrittenUnder = claim;

        if (Owns(key, claim))
            _entries.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> UnheldAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        Calls.Add($"unheld {keys.Count}");
        Tokens["unheld"] = ct;

        IReadOnlyCollection<string> unheld = [.. keys.Where(key => !_entries.ContainsKey(key))];

        return Task.FromResult(unheld);
    }

    /// <summary>Plants a completed entry, standing in for an earlier attempt that committed.</summary>
    public void Completed(string key, string payload) =>
        _entries[key] = new Held(Planted, new IdempotencyEntry(false, payload));

    /// <summary>Plants a claim nobody finished — the in-flight case.</summary>
    public void InFlight(string key) =>
        _entries[key] = new Held(Planted, new IdempotencyEntry(true, null));

    /// <summary>A planted entry's token, which no test passes, since the entry belongs to another attempt.</summary>
    private const string Planted = "planted-by-another-attempt";

    private bool Owns(string key, string claim) =>
        _entries.TryGetValue(key, out Held? held) && held.Token == claim;

    private sealed record Held(string Token, IdempotencyEntry Entry);
}

/// <summary>A caller, with a factory per case §8.5's subject segment distinguishes.</summary>
internal sealed class StubCurrentUser : ICurrentUser
{
    private readonly Guid? _id;

    private StubCurrentUser(Guid? id)
    {
        _id = id;
    }

    public bool IsAuthenticated => _id is not null;

    public Guid Id => _id ?? throw new InvalidOperationException("No authenticated caller.");

    public static StubCurrentUser Authenticated(Guid id) => new(id);

    /// <summary>No caller: an anonymous HTTP request or a message-borne command alike (§8.5).</summary>
    public static StubCurrentUser Anonymous() => new(null);

    public bool HasPermission(string permission) => false;
}
