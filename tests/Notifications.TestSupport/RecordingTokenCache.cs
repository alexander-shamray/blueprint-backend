using Common.Infrastructure.Identity;

namespace Notifications.TestSupport;

/// <summary>An <see cref="ITokenCache"/> that answers without a network call, a different token each time.</summary>
/// <remarks>Distinct, so the attempts §11.5's retried handler makes can be told apart.</remarks>
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
