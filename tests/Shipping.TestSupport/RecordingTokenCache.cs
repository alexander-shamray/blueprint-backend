using Common.Infrastructure.Identity;

namespace Shipping.TestSupport;

/// <summary>
/// An <see cref="ITokenCache"/> that answers without a network call and hands
/// out a different token each time.
/// </summary>
/// <remarks>
/// The distinct tokens are an instrument, not a model: §11.5 puts the
/// credential handler inside the resilience pipeline so a retried attempt
/// goes back to the cache, and only attempts that can be told apart show it.
/// </remarks>
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
