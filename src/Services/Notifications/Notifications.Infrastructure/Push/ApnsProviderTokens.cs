using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Push;

/// <summary>APNs' provider token, an ES256 JWS minted from the team's key and reused for a lifetime.</summary>
/// <remarks>
/// Apple refuses a token renewed more often than every twenty minutes or used past sixty, so it is renewed at
/// <see cref="ApnsHop.ProviderTokenLifetime"/>, and at once only after APNs answers that one expired.
/// </remarks>
internal sealed class ApnsProviderTokens : IDisposable
{
    private readonly ApnsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ECDsa _key;
    private readonly Lock _gate = new();

    private Issued? _issued;

    public ApnsProviderTokens(IOptions<ApnsOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;

        // A missing key reaches here in Development alone: AddPushChannel refuses it anywhere else before start.
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (!string.IsNullOrWhiteSpace(_options.PrivateKey))
            _key.ImportFromPem(_options.PrivateKey);
    }

    public string Get()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (Volatile.Read(ref _issued) is { } held && now < held.RenewAt)
            return held.Token;

        lock (_gate)
        {
            if (Volatile.Read(ref _issued) is { } again && now < again.RenewAt)
                return again.Token;

            Issued issued = new(Mint(now), now + ApnsHop.ProviderTokenLifetime);
            Volatile.Write(ref _issued, issued);
            return issued.Token;
        }
    }

    /// <summary>Drops <paramref name="refused"/> while held, so a late answer never drops a newer one.</summary>
    public void Invalidate(string refused)
    {
        if (Volatile.Read(ref _issued) is { } held && held.Token == refused)
            Interlocked.CompareExchange(ref _issued, null, held);
    }

    // SignData's default is IEEE P1363's fixed-width r || s, the form RFC 7518 section 3.4 asks of ES256.
    internal string Mint(DateTimeOffset now) =>
        Jws.Sign(
            new Dictionary<string, object> { ["alg"] = "ES256", ["kid"] = _options.KeyId! },
            new Dictionary<string, object> { ["iss"] = _options.TeamId!, ["iat"] = now.ToUnixTimeSeconds() },
            data => _key.SignData(data, HashAlgorithmName.SHA256));

    public void Dispose() => _key.Dispose();

    private sealed record Issued(string Token, DateTimeOffset RenewAt);
}
