using Common.Infrastructure.Identity;

namespace Web.Bff.Tests;

/// <summary>An <see cref="ITokenCache"/> with a new token each ask, so a retried attempt shows (§11.5).</summary>
/// <remarks>A test instrument: the real cache serves one token until its expiry guard (§11.5).</remarks>
public sealed class RecordingTokenCache : ITokenCache
{
    private int _issued;

    /// <summary>Every scope this has been asked for, in order.</summary>
    public List<string> Scopes { get; } = [];

    /// <summary>How many tokens have been handed out.</summary>
    public int Issued => _issued;

    public Task<string> GetAsync(string scope, CancellationToken ct)
    {
        lock (Scopes)
        {
            Scopes.Add(scope);
        }

        return Task.FromResult($"token-{Interlocked.Increment(ref _issued)}");
    }
}
