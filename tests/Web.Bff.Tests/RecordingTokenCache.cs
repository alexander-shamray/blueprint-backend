using Common.Infrastructure.Identity;

namespace Web.Bff.Tests;

/// <summary>
/// An <see cref="ITokenCache"/> that answers without a network call and hands
/// out a different token each time.
/// </summary>
/// <remarks>
/// <b>The distinct tokens are the point, not the absence of a provider.</b>
/// §9.7 and §11.5 both claim that the credential handler sits inside the
/// resilience pipeline, so a retried attempt goes back to the cache instead of
/// replaying the token the first attempt built. That claim is only observable
/// if two attempts can be told apart, and a constant token makes the correct
/// pipeline and the reversed one produce byte-identical requests — the shape
/// this repository keeps finding: a test that cannot fail in one direction.
/// <para>
/// <b>Answering differently every time is a test instrument and not a model of
/// production.</b> The real <c>CachingTokenClient</c> hands back the same token
/// until its expiry guard, and its own tests assert exactly that — so two
/// attempts milliseconds apart normally carry identical bytes. What this makes
/// visible is that the handler <i>ran</i> again, which is the thing the
/// ordering decides; refreshing an expired token is what that buys, and it is
/// rarer than every wording of this claim used to suggest.
/// </para>
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
